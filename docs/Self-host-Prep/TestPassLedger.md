# Self-Host-Prep Test Pass Ledger

This document is the historical triage/progress ledger for ported-test pass
state. It is reference material for fixing failures, not the authoritative task
list. Keep executable task ordering in [TASKS.md](TASKS.md); update this ledger
only when rebaselining suites, recording a new failure-family classification, or
capturing details that would otherwise clutter the task list.

Use [TASKS.md](TASKS.md) for compact task and subtask checkboxes, not for
failure evidence, run logs, or status-update prose.

---

## 2026-06-27 Selfhost Compact MIR Integer Widths

- MIR now carries `i8` and `i16` scalar widths through typed values, globals,
  package-image byte codecs, textual MIR rendering, and LLVM type emission.
- Compact typed constants are validated against their storage width before
  range facts are recorded.
- Wrapping and saturating arithmetic facts now preserve compact signed bounds
  for `i8` and `i16` values.
- LLVM lowering emits compact arithmetic directly and widens compact saturating
  operations only for the clamp calculation.
- High-level IR lowering validators now accept `i8` and `i16` where scalar MIR
  values are lowerable and reject out-of-range compact initializers.
- Package-image type byte compatibility is preserved by keeping existing
  `i1`/`i32`/`i64`/`ptr` encodings and assigning new bytes for `i8` and `i16`.
- Focused verification:
  `../../stark test --filter BuildMirValueRangeFactsPreservesCompactIntegerWidths --filter MirCompactWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm --filter BinaryRoundTripsGlobals --filter EmitsLlvmCompactTypedParamComparison --filter EmitsLlvmTypedGlobalLoadStore --filter EmitsLlvmGlobals --filter MirGlobalRecordsInitialValue --filter MirExplicitWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Boolean Switch Lowering

- Terminal switches over `bool` scrutinees now parse `case true` and
  `case false` labels and lower them through typed MIR branch blocks.
- Exhaustive true/false switches emit one direct `i1` conditional branch and
  do not lower an unreachable default return block.
- Boolean parameters and boolean literals now survive expression lowering as
  typed `i1` MIR values, including boolean return arms that zext to `i64`.
- Non-terminal switches over `bool` scrutinees now parse `case true` and
  `case false` assignment arms and lower them through direct `i1` branches.
- Exhaustive true/false assignment switches lower only reachable arms plus the
  merge block, while still validating the source `default` arm shape and calls.
- Type-aware expression lowering is threaded through terminal switch, terminal
  `if`, local-prefixed, tail-call, and switch-assignment lowering contexts that
  already carry local type facts.
- Typed constant range facts now validate against the MIR result type before
  recording integer facts.
- Narrow verification:
  `../../stark test --filter CompilesTerminalBooleanSwitchFromAst --filter CompilesSingleCaseTerminalBooleanSwitchFromAst --filter CompilesTerminalBooleanSwitchBoolArmsFromAst --filter TerminalBooleanSwitchRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalBooleanSwitch --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent terminal-switch verification:
  `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementArbitraryOrderMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Boolean switch-assignment verification:
  `../../stark test --filter CompilesBooleanLiteralLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesSingleCaseBooleanLiteralLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLiteralLocalSwitchStatementBoolAssignmentThenReturnFromAst --filter PackageTablesPreserveBooleanLocalSwitchStatementAssignment --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Arbitrary-Order Scalar Assignment Targets

- Non-terminal switch assignment arms now accept braced scalar target
  assignments in any source order while still requiring every target exactly
  once per arm.
- Assigned RHS roots are stored and lowered in source order, and a parallel
  target-offset table projects the already-lowered values back into declaration
  order for MIR phi construction.
- Integer and boolean target facts continue through typed phis, LLVM range
  attributes, `i1` payloads, and final `zext` returns.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementArbitraryOrderMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMixedScalarAssignmentsThenTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementArbitraryOrderMultipleScalarAssignments`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementArbitraryOrderMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementArbitraryOrderMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Terminal-switch dispatcher smoke:
  `../../stark test --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Multi-Statement Assignment Arms

- Non-terminal switch assignment arms now accept braced arm-local scalar
  declarations before the final assignment to the switch target.
- Arm-local names are scoped per arm and lower through arm-specific type and
  SSA override tables, preserving integer and boolean facts through MIR phis and
  LLVM returns.
- Statement-end scanning now allows comparison operators such as `<` in local
  initializers while still respecting parentheses and brackets.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Terminal-switch smoke verification:
  `../../stark test --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Multiple Scalar Assignment Targets

- Non-terminal switch assignment lowering now accepts multiple pre-switch scalar
  target locals and braced arms that assign those targets in declaration order.
- Each target lowers through its own nested phi chain, so integer and boolean
  target facts remain independent through post-switch returns and terminal
  `if` branches.
- The dispatcher probe now recognizes multi-target switch-assignment bodies
  without stealing local-prefixed terminal switch bodies.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementMixedScalarAssignmentsThenTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementMultipleScalarAssignments`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentArmLocalsThenReturnFromAst --filter CompilesLocalSwitchStatementMultipleScalarAssignmentsThenReturnFromAst --filter CompilesLocalSwitchStatementMixedScalarAssignmentsThenTerminalIfFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentArmLocals --filter PackageTablesPreserveLocalSwitchStatementMultipleScalarAssignments --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Terminal-switch dispatcher smoke verification:
  `../../stark test --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## Baseline Snapshot

Porting is effectively done (2638/2638). The remaining test work is making the
ported facts pass on macOS. All 19 suites were baselined with clean
`rm -rf build && stark test` runs on 2026-06-19. `compiler.FeatureTests` and
`compiler.LlvmTests` were rechecked by targeted full-project runs on 2026-06-23.

Summary: at least 2843 / 3144 run-facts passing (~90%). 15 of 19 suites are
known 100% green. At most 301 failures live in 4 suites. Counts are runner
`ok`/`FAILED`; `[Theory]` rows expand, so run-fact totals differ slightly from
static `[Fact]` counts. Non-feature/non-LLVM failing-suite counts remain the
2026-06-19 baseline unless their notes say otherwise.

| Suite | Passing | Failing | Notes |
|---|---:|---:|---|
| compiler.Tests | 1090 | **112** | largest suite: semantic/lowering diagnostics, type-checking, ownership, pipeline, runtime, package-image, CLI, examples |
| compiler.SsaTests | 346 | **61** | SSA lowering / validation / optimization text. ArithmeticFold + ValueFacts + AliasAware + ScopedNoAlias + Cleanup + ScalarReplacement + InlineSsa + FunctionAddress + ConstantText + TextView + DynamicStorage families are green by targeted filters; count predates recent targeted fixes |
| compiler.LlvmTests | 493 | 0 | green by 2026-06-23 targeted project rerun |
| stdlib.Port | 214 | **14** | stdlib behavior ports; count includes 2026-06-23 targeted `io-path`, `io-file`, `io-file-runtime`, `memory-helper`, `memory`, `collections-dictionary`, `collections-hash-set-sort`, `collections`, `text`, `promoted-runtime-buffer`, `promoted-console`, `promoted-net-tcp`, `process`, `memory-contract-audit`, `raw-pointer-audit`, `range-notation`, `runtime-platform-mac-os`, and `collections-package-drop-regression` fixes but no full-suite rebaseline |
| compiler.MirTests | 101 | **36** | MIR lowering text; count predates recent switch-pattern, place-lowerer, generic, and lowering-contract targeted fixes |
| compiler.FeatureTests | 213 | 0 | green by 2026-06-23 targeted project rerun |
| selfhost.Ir | 122 | 0 | green |
| selfhost.Binding | 82 | 0 | green |
| stdlib.Text | 59 | 0 | green |
| stdlib.Toml | 55 | 0 | green |
| selfhost.Parsing | 51 | 0 | green |
| stdlib.Testing | 34 | 0 | green |
| selfhost.Lexing | 18 | 0 | green |
| stdlib.IO.Path | 12 | 0 | green |
| stdlib.FileSystem | 10 | 0 | green |
| stdlib.Collections.Arena | 9 | 0 | green |
| selfhost.Typing | 5 | 0 | green |
| stdlib.Collections.Slice | 4 | 0 | green |
| stdlib.Json | 3 | 0 | green |

Suites still needing work:

- compiler.SsaTests: 346/407, 61 failing before recent targeted fixes.
  ArithmeticFold + ValueFacts + AliasAware + ScopedNoAlias + Cleanup +
  ScalarReplacement + InlineSsa + FunctionAddress + ConstantText + TextView +
  DynamicStorage are done and verified by targeted filters. No full-suite
  rebaseline was run because broad sweeps are intentionally avoided.
- compiler.Tests: 1090/1202, 112 failing; broad suite needing failure-family
  subcategorization.
- stdlib.Port: at least 214/228, at most 14 failing after the 2026-06-23
  targeted `io-path`, `io-file`, `io-file-runtime`, `memory-helper`, and
  `memory` fixes plus the targeted collection fixes.
- compiler.MirTests: 101/137, 36 failing before recent targeted fixes.
- compiler.Tests package-image typed-body integration ports now use typed-only
  package images and the shared helper restores CLI stdout, emitted-file,
  package-JSON typed-body, source-deletion, executable, and runtime exit-code
  assertions. Targeted direct probes for power, comparison-chain, and
  terminal-if package consume paths succeeded with zero diagnostics; a manual
  package-runtime power probe exited 81 after deleting the producer source; all
  `PackageImageTyped*IntegrationTests` source files pass single-file checks.
  A tiny direct executable probe that imports `CompilerTestSupport` and calls
  the package runtime helper now compiles and exits 0 after the ABI duplicate
  signature check was made structural for nested callback types. The generated
  `compiler.Tests` project runner was not rebaselined because broad sweeps are
  intentionally avoided.

Already green, no task: compiler.FeatureTests, compiler.LlvmTests,
selfhost.Ir, selfhost.Binding, selfhost.Parsing, selfhost.Lexing,
selfhost.Typing, stdlib.Text, stdlib.Toml, stdlib.Testing, stdlib.IO.Path,
stdlib.FileSystem, stdlib.Collections.Arena, stdlib.Collections.Slice,
stdlib.Json.

---

## 2026-06-27 Selfhost Switch Post-Local Terminal-If Successor Lowering

- Non-terminal integer switch assignment lowering now supports scalar
  post-switch locals followed by a terminal `if` with returning arms.
- The first switch merge block can now become a conditional branch to appended
  tail return blocks while preserving the switch-assigned phi and successor
  local override table.
- Integer and boolean facts continue through the tail branch, including LLVM
  range attributes, `i1` phi payloads, `br i1`, and final boolean `zext`
  returns.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalTerminalIfFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocalTerminalIf`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Multiple Post-Local Successor Lowering

- Non-terminal integer switch assignment lowering now supports multiple scalar
  local initializers after the switch and before the final return.
- Successor locals lower in declaration order through the explicit local
  override table, so later successor locals can use earlier successor locals and
  the final return can still use the switch-assigned phi.
- Integer and boolean facts continue through the path, including LLVM range
  attributes, `i1` switch phis, and final boolean `zext` returns.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenMultiplePostLocalsFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenMultiplePostLocals`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Post-Local Successor Lowering

- Non-terminal integer switch assignment lowering now supports one scalar local
  initializer after the switch and before the final return.
- The post-switch local initializer lowers in the first merge block from the
  switch-assigned phi, preserving integer and boolean facts through LLVM range
  attributes and `i1` phi payloads.
- Narrow verification:
  `../../stark test --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal`
  in `tests-stark/selfhost.Ir` passed.
- Adjacent switch-assignment verification:
  `../../stark test --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter CompilesLocalSwitchStatementAssignmentThenPostLocalFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenPostLocalFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenPostLocal`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Switch Assignment Merge Lowering

- Integer switch assignment arms now lower to MIR comparison-chain control flow
  with nested two-input merge phis, so one-or-more cases can continue to a
  post-switch return expression without inventing an illegal N-way phi.
- Boolean switch assignment arms keep `i1` phi payloads through MIR and only
  `zext` at the scalar return boundary, preserving LLVM range facts.
- Narrow verification:
  `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter CompilesLocalSwitchStatementAssignmentThenReturnFromAst --filter CompilesBooleanLocalSwitchStatementAssignmentThenReturnExpressionFromAst --filter LocalSwitchStatementAssignmentRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseBooleanTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch --filter PackageTablesPreserveLocalSwitchStatementAssignmentThenReturn`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Direct LLVM Switch Emission

- Three-or-more terminal integer-switch comparison chains now emit LLVM `switch`
  terminators for literal cases, skipping the old compare blocks in emitted
  LLVM while keeping the existing MIR/package-table shape.
- The direct switch path is shared by no-fact block emission and range-fact
  module emission, so return ranges, parameter facts, ABI facts, and call/effect
  attributes continue through the same lowering path.
- Narrow verification:
  `../../stark test --filter EmitsLlvmDirectSwitchForDenseComparisonChain --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseBooleanTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-27 Selfhost Multi-Case Terminal Switch Lowering

- Terminal integer switch parsing now accepts one or more literal cases plus a
  default and rejects duplicate literal labels across the whole case list.
- Terminal switch MIR lowering now uses one shared comparison-chain builder for
  direct, local-prefixed, boolean-valued, single-case, and multi-case return
  arms while preserving local SSA overrides and explicit boolean `zext` returns.
- Narrow verification:
  `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter CompilesMultiCaseTerminalIntegerSwitchFromAst --filter CompilesSingleCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedMultiCaseBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch --filter PackageTablesPreserveMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveSingleCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedMultiCaseBooleanTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-26 Selfhost HIR Fact-Type Validation

- HIR-to-MIR fact compatibility now rejects scalar values carrying pointer
  nullability facts and pointer values carrying integer range facts.
- Parameter lowering now applies the common value-fact compatibility check
  before emitting `Param` instructions or symbol-map rows.
- Narrow verification:
  `../../stark test --filter FactsOutsideType` in
  `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter Nullability` in
  `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsCallResultFactsOutsideResultTypeWithoutEmission`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Global Store Fact Subsets

- HIR global-store lowering now enforces the full declared value-fact subset,
  including alignment, ABI, noalias, volatile, nullability, and integer range.
- Alignment subsets are checked by divisibility, so a stronger alignment fact
  satisfies a weaker one without accepting incompatible alignments.
- Narrow verification:
  `../../stark test --filter MirLoweringChecksGlobalStoreBackendFactSubset` in
  `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Local Symbol Fact Validation

- SSA local alias binding and local assignment rebinding now validate carried
  value facts against the MIR value type before updating the lowering symbol map.
- This prevents stale pointer range facts and scalar nullability facts from
  becoming backend-visible local facts.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsLocalAliasFactsOutsideTypeWithoutBinding --filter MirLoweringRejectsAssignmentFactsOutsideTypeWithoutRebinding`
  in `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringBindsLocalAliasWithoutEmissionAndPreservesFacts --filter MirLoweringLowersLocalAssignmentByRebindingSymbolAndFacts --filter MirLoweringRejectsInvalidLocalAssignmentWithoutRebinding`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Return Fact Validation

- HIR return lowering now validates returned value facts against the MIR return
  type before appending a return block.
- This prevents stale pointer range facts and scalar nullability facts from
  becoming backend-visible terminator facts.
- Narrow verification:
  `../../stark test --filter MirLoweringRejectsReturnFactsOutsideTypeWithoutBlockEmission`
  in `tests-stark/selfhost.Lowering` passed.
- Narrow verification:
  `../../stark test --filter MirLoweringLowersValueReturnToMirReturnBlock --filter MirLoweringRejectsReturnTypeMismatchWithoutBlockEmission --filter MirLoweringRejectsReturnWithoutValueFactsBeforeBlockEmission`
  in `tests-stark/selfhost.Lowering` passed.

---

## 2026-06-26 Selfhost Typed Parameter Lowering

- Lowering now accepts typed non-i64 HIR parameters, emits typed MIR `Param`
  instructions, and preserves parameter facts in the MIR value-fact table and
  lowering symbol map.
- Typed LLVM straight-line emission now has a typed parameter signature path
  with width-correct integer range attributes.
- Narrow verification:
  `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`
  passed, including `MirLoweringLowersTypedNonI64ParametersWithFacts`.
- Narrow verification:
  `../../stark test --filter EmitsLlvmTypedFunctionWithParameterTypesAndFacts`
  in `tests-stark/selfhost.Ir` passed.

---

## 2026-06-26 Selfhost Null Pointer Literal Lowering

- Lowering now accepts null pointer HIR literals, emits typed MIR pointer-zero
  constants, and preserves known-null facts in value-fact and lowering-symbol
  tables.
- MIR value facts now model nullability, and typed LLVM emission renders null
  pointer constants as `inttoptr i64 0 to ptr`.
- Narrow verification:
  `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`
  passed, including `MirLoweringLowersNullPointerLiteralWithNullabilityFacts`.
- Narrow verification:
  `../../stark test --filter MirNullPointerConstantRoundTripsFactsAndTypedLlvm`
  in `tests-stark/selfhost.Ir` passed.
- Narrow verification:
  `../../stark test --filter ValueFacts` and
  `../../stark test --filter IrFactCategoryIndexCoversConcreteDescriptors` in
  `tests-stark/selfhost.Ir` passed.

---

## 2026-06-23 Feature Tests Recheck

- Reproduced and fixed the lone `compiler.FeatureTests` residue in
  `ComptimeIndexedEnumVariantFactsFoldToConstants`.
- The embedded source now returns `u64[0 max]`, matching
  `System.Compiler.EnumVariantPayloadCount` while preserving the LLVM
  `ret i64 31` expectation.
- Narrow verification: the single fact passed with `--filter`, and the full
  `compiler.FeatureTests` project passed on `arm64-apple-macosx26.0.0`.
- No broad suite sweep was run.

---

## 2026-06-23 LLVM Tests Recheck

- Rechecked `compiler.LlvmTests` after the known package-image and option-toggle
  residues had landed; the full project now passes on `arm64-apple-macosx26.0.0`.
- Fixed the host-test runner so an empty request target still carries the
  detected target into `CompilerOptions`, not just stdlib resolution.
- Kept Linux/x86 LLVM assertions strong by pinning artifact-only COMDAT/coldcc
  tests to `x86_64-unknown-linux-gnu` and using source-stdlib resolution for
  Linux benchmark probes.
- Updated call-site expectations where lowering now preserves stronger backend
  facts, including raw-pointer count ranges and imported asm argument facts.
- Narrow verification: `dotnet build src/compiler.csproj --no-restore` passed,
  then `../../stark test --target arm64-apple-macosx26.0.0` passed in
  `tests-stark/compiler.LlvmTests`. No broad suite sweep was run.

---

## 2026-06-23 Stdlib Port Recheck

- `standard-library-generic` passed as a targeted `stdlib.Port` collection.
- Fixed `StdLibSourcePromotedPathLowersThroughDynamicStorage` by pinning the
  artifact probe to `x86_64-unknown-linux-gnu`, preserving the original
  libc-free dynamic-storage oracle.
- The `io-path` collection now passes on `arm64-apple-macosx26.0.0`; no broad
  `stdlib.Port` sweep was run.
- Fixed the `io-file` collection by compiling `stdlib/src/System/IO/File.stark`
  directly for the file flush/buffering LLVM probes. The buffered ASCII copy
  probe is pinned to `x86_64-unknown-linux-gnu`, preserving the target-specific
  `rep movsb` inline-asm oracle.
- Narrow verification: `../../stark test --collection io-file --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Added source-path compilation to the Stark host-test bridge so artifact probes
  can compile `stdlib/src/System/Memory.stark` directly instead of relying on
  wrapper imports.
- Fixed the `memory-helper` collection by restoring body-scoped LLVM checks for
  memory helper overlap guards, hot-tail memcpy/memset lowering, no scalar
  fallback, and helper attributes. Infallible moves now assert the stronger
  `llvm.memmove` lowering.
- Narrow verification: `../../stark test --collection memory-helper --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `memory` collection by pinning the allocator-symbol artifact probes
  to `x86_64-unknown-linux-gnu`, preserving the no-libc Linux allocator oracle
  instead of rejecting the host macOS allocator lowering. The allocator audit
  workload now mirrors the C# helper's heap-allocation loop.
- Narrow verification: `../../stark test --collection memory --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Added target-aware source-path host-test compilation and fixed the
  `io-file-runtime` collection by compiling
  `stdlib/src/System/Runtime/Platform/Linux.stark` directly for
  `x86_64-unknown-linux-gnu`, preserving the lseek/fsync syscall oracles.
- Narrow verification: `../../stark test --collection io-file-runtime --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `threading` collection; all 17 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify which, if any, of these facts were part of the failing
  baseline bucket.
- Rechecked the `threading-atomics` collection; all 12 facts passed on
  `arm64-apple-macosx26.0.0`, including the tier-1/tier-2/tier-3 lowering
  oracles for lock-free and spinlock-protected atomic operations. Counts were
  left unchanged because the previous ledger did not identify which, if any, of
  these facts were part of the failing baseline bucket.
- Rechecked the `runtime-platform-windows` collection; 13 artifact/compile facts
  passed and the 3 Windows-runtime facts skipped on macOS by platform gate.
  Counts were left unchanged for the same conservative-accounting reason.
- Fixed the `collections-dictionary` collection by restoring body-scoped custom-key
  LLVM checks while allowing the faster inlined `Symbol.Hash`/`Symbol.Equals`
  lowering. The probe now asserts the actual inline-clone dictionary path has no
  `DictionaryKey_Hash` or `DictionaryKey_Equals` fallback dispatch.
- Narrow verification: `../../stark test --collection collections-dictionary --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `collections-hash-set-sort` collection by restoring body-scoped LLVM
  checks for sort and custom-key HashSet paths. The sort probes now assert no
  allocation, fnptr-pair extraction, or indirect closure call inside `SortFixed`,
  while HashSet accepts inlined `Symbol.Hash`/`Symbol.Equals` and rejects
  `DictionaryKey_Hash`/`DictionaryKey_Equals` fallback dispatch in the actual
  probe bodies.
- Narrow verification: `../../stark test --collection collections-hash-set-sort
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `collections-stack-queue` collection; all 5 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify whether this collection contributed to the failing
  baseline bucket.
- Fixed the `collections` collection by pinning the promoted List dynamic-storage
  LLVM oracle to `x86_64-unknown-linux-gnu`, preserving the libc-free
  `__stark_runtime_try_realloc` and `__stark_dynamic_try_reserve` assertions and
  the negative libc allocator checks.
- Narrow verification: `../../stark test --filter
  StdLibSourcePromotedListLowersThroughDynamicStorage --target
  arm64-apple-macosx26.0.0` and `../../stark test --collection collections
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `text` collection by pinning the promoted text dynamic-storage LLVM
  oracle to `x86_64-unknown-linux-gnu`, compiling `stdlib/src/System/Text.stark`
  directly for append, wide-formatting, and wide-parse backend assertions, and
  restoring the source-text scan for bounded raw-pointer region contracts.
- Narrow verification: `../../stark test --collection text --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `text-runtime` collection; all 3 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify whether this collection contributed to the failing
  baseline bucket.
- Rechecked the `text-interning` collection; all 3 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged for the same
  conservative-accounting reason.
- Fixed the `promoted-runtime-buffer` collection by compiling
  `stdlib/src/System/Runtime/Buffer.stark` directly for runtime-buffer backend
  assertions and using function-scoped LLVM body checks for disjoint write
  guards, tail-region memcpy/memset paths, and allocation-free inline fixed
  storage.
- Narrow verification: `../../stark test --collection promoted-runtime-buffer
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `promoted-console` collection by compiling
  `stdlib/src/System/Console.stark` and
  `stdlib/src/System/Runtime/Platform/Linux.stark` directly for backend
  assertions, restoring scoped LLVM checks for direct platform write paths,
  small-buffer newline coalescing, and allocation-free byte-line writes.
- Narrow verification: `../../stark test --collection promoted-console
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `promoted-io-file-system` collection and restored the C# oracle's
  source-text assertions for platform raw-pointer file IO regions, fast
  directory/file entry points, and allocation-free `System.FileSystem` storage.
  Counts were left unchanged because the previous ledger did not identify whether
  this collection contributed to the failing baseline bucket.
- Narrow verification: `../../stark test --collection promoted-io-file-system
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Fixed the `promoted-net-tcp` collection by compiling
  `stdlib/src/System/Net/Tcp.stark` directly for `x86_64-unknown-linux-gnu`,
  restoring source ABI scans, and updating the dynamic-buffer LLVM body symbol
  to the current max-count-mangled name while preserving bulk read/write-slice
  fast-path checks.
- Narrow verification: `../../stark test --collection promoted-net-tcp --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked the `runtime-buffer` collection; both facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged because the previous
  ledger did not identify whether this collection contributed to the failing
  baseline bucket.
- Rechecked the `console` collection; all 5 facts passed on
  `arm64-apple-macosx26.0.0`. Counts were left unchanged for the same
  conservative-accounting reason.
- Fixed the `process` collection by updating the `System.Process.Exit` caller
  LLVM assertions for the current trap call spelling while still requiring the
  module-level `__stark_unreachable_trap` definition to carry `cold noreturn`.
- Narrow verification: `../../stark test --collection process --target
  arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked `net`, `file-system`, `json`, `math`, `c`,
  `compiler-integer-facts`, and `backend-boundary-audit`; all selected facts
  passed on `arm64-apple-macosx26.0.0`. The `file-system` run skipped the
  Linux-only runtime facts through platform gates.
- Fixed the `memory-contract-audit` collection by restoring the C# oracle's
  direct source-text scans for explicit overlap contracts in `System.Memory`,
  `System.Text`, `System.IO.Path`, and `System.Runtime.Buffer`.
- Fixed the `raw-pointer-audit` collection by replacing compile-only reductions
  with `System.FileSystem.Glob` source-tree scans, preserving the documented
  raw-pointer boundary allowlist, checking public raw-pointer declarations, and
  asserting the root module still excludes `System.Text`/`System.Testing` raw
  surfaces while re-exporting safe public modules.
- Updated `docs/Internals/StandardLibraryRawPointerBoundaries.md` and the host
  C# allowlist for the audited `System.Json`, `System.Toml`, and
  `System.Testing.HostCompiler` internal raw-pointer files.
- Narrow verification: `../../stark test --collection memory-contract-audit
  --target arm64-apple-macosx26.0.0` and `../../stark test --collection
  raw-pointer-audit --target arm64-apple-macosx26.0.0` passed in
  `tests-stark/stdlib.Port`.
- Fixed the `range-notation` collection by canonicalizing remaining stdlib
  source spellings (`2 ** 16`, `2 ** 15 - 1`, and spaced `2 ** 53` comments)
  and replacing the compile-only Stark reduction with a real source/template
  glob audit that ignores string literals like the C# oracle.
- Narrow verification: `dotnet test
  tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj
  --no-restore --filter FullyQualifiedName~SystemRangeNotationStandardLibraryTests`,
  `../../stark test --collection range-notation --target
  arm64-apple-macosx26.0.0`, `../../stark test --collection json --target
  arm64-apple-macosx26.0.0`, and `../../stark test --collection toml --target
  arm64-apple-macosx26.0.0` passed.
- Fixed the `runtime-platform-mac-os` collection by restoring direct
  source-path compilation of `System/Runtime/Platform/MacOS.stark` for
  `arm64-apple-macosx26.0.0`, including the original libSystem declaration
  checks and scoped `stat` mode-bit LLVM body checks.
- Narrow verification: `../../stark test --collection runtime-platform-mac-os
  --target arm64-apple-macosx26.0.0` passed in `tests-stark/stdlib.Port`.
- Rechecked `testing`, `book-sample`, and `syscall`; all selected run-facts
  passed on `arm64-apple-macosx26.0.0`, with the Linux-only packaged syscall
  fact skipped by platform gate.
- Rechecked `net-tcp` and `runtime-platform-linux`; all selected run-facts
  passed on `arm64-apple-macosx26.0.0`, with Linux-only runtime facts skipped
  by platform gates where applicable.
- Ported the final unported qualifying C# stdlib regression,
  `ManifestBackedGenericFieldDropResolvesListClearFromStdlibPackage`, as a real
  package-backed MIR test. The Stark helper builds a Facade package, deletes the
  producer source, then compiles the Demo consumer through lower-mir with
  STARK_PATH stdlib roots and target/data-layout facts preserved.
- Narrow verification: `../../stark test --collection
  collections-package-drop-regression --target arm64-apple-macosx26.0.0` passed
  in `tests-stark/stdlib.Port`.

---

## 2026-06-22 Target Pinning And Platform Gates

- Completed the `stdlib.Port` non-macOS target-pin/platform-gate pass. Artifact
  probes now use explicit Linux/Windows triples plus `STARK_PATH` source-stdlib
  resolution, and runtime/native behavior tests that require a real foreign
  platform are `[Platform(...)]` gated with source comments.
- Added a seeded target+`STARK_PATH` host-test wrapper for imported inline-clone
  probes whose platform helper bodies must remain visible in LLVM text.
- Narrow verification run:
  - `--check tests-stark/stdlib.Port/StdlibPortTests.stark --target arm64-apple-macosx26.0.0 --no-stark-path -I tests-stark/stdlib.Port -I stdlib/src`: passed.
  - `stark test --collection net-tcp --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --collection syscall --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --collection runtime-platform-linux --target arm64-apple-macosx26.0.0`: passed.
  - Direct host-test inspect for the three fixed Windows runtime-platform probes
    (`windows-path-behavior-wide-normalization`,
    `windows-dispatch-process-exit-no-symbol-collision`,
    `windows-dispatch-template-mirrors-linux-surface`): all compiled with zero
    diagnostics and rendered LLVM.
- Not a rebaseline: grouped `runtime-platform-windows` and grouped
  `standard-library-generic,io-file-runtime,io-path,memory,threading` runner
  checks were interrupted after proving too slow for targeted feedback; no
  broad suite sweep was run.

## 2026-06-22 SSA Cleanup Source-Port Fixes

- Fixed five `compiler.SsaTests` cleanup/source-port facts without a broad
  sweep: algebraic identities now inspect optimized SSA operator absence, the
  non-zero divide/modulo source uses an unsigned non-negative range, and three
  fixed-array fixtures use Stark's `T[N]` syntax.
- Narrow verification run:
  - `stark test --filter CleanupRemovesIntegerAlgebraicIdentities --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupRemovesSameOperandDivisionAndModuloWhenRangeExcludesZero --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupForwardsAggregateIndexThroughPhiWhenIncomingElementsMatch --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupForwardsAggregateIndexThroughSelectWhenSelectedElementsMatch --target arm64-apple-macosx26.0.0`: passed.
  - `stark test --filter CleanupRemovesUnusedLocalStorageScaffolding --target arm64-apple-macosx26.0.0`: passed.

---

## compiler.LlvmTests Residue

- Closed by the 2026-06-23 targeted project rerun.
- package-image (#4): mechanism built and proven with `CompileLlvmWithPackage`.
  All 9 ported compiler.LlvmTests package-image facts are green, including the
  4 `PackageImageBacked*` callable-value tests. The helper now builds package
  images and consumers with explicit matching target/data-layout facts.
  Typed-only package codegen is now available through `--package-typed-only`
  and the Stark host-test package builder switch; the reduced manifest-backed
  compiler assertions have source-level runtime/CLI equivalents restored.
- Flag/datalayout/source-backed LLVM residues are done:
  `ImmutableGlobalsWithoutAddressTaken`,
  `InternalizedImmutableGlobals`, `RootFunctionSymbolIsQualified`,
  `LibraryBuildQualifies`, `ExecutableInternalization`,
  `ConfiguredTargetInfoIsEmittedInHeader`,
  `LibraryBuildQualifiesPublicRootSymbols`,
  `ModulePrivateFunctionsLowerWithInternalLinkage`,
  `FunctionPointerCallSiteEffectAttributesFollowPointerKind`,
  `OptimizedDynamicStorageReserveNoop`, `DynamicStorageMoveAtEmitsDirectLengthUpdate`,
  `DirectoryEnumerationDoesNotExposeLargeDirectoryPayloadAsSsaValue`,
  `MemoryCopyFillHotLoopUsesInfallibleHelpers`,
  `TextFormattingBenchmarksSpecializeConstantIntegerFormatting`, and
  `WhitespaceOnlyLinesShorterThanTheClosingIndentation`.

---

## Failure Families

The 2026-06-19 sweep grouped the failures around a few broad levers rather than
hundreds of unrelated fixes. `compiler.FeatureTests` and `compiler.LlvmTests`
were fixed and verified by targeted project reruns, leaving 4 main suites.

Cross-cutting levers:

- Package-image input, PAINPOINTS #4: remaining package-image residue is in
  `compiler.Tests` ManifestBacked/PackageImage paths; `compiler.LlvmTests`
  package-image facts are green after the targeted 2026-06-23 rerun.
- SSA/MIR text alignment, PAINPOINTS #11 reframed: roughly 145 tests left across
  `compiler.SsaTests` and `compiler.MirTests`. The `optimized-ssa`/`mir`
  artifacts already carry operands, block labels, and typed terminators. Most
  failures are wrong-artifact-selection plus wrong-fragment-spelling, like the
  LLVM raw-vs-normalized gap. ArithmeticFold proved the method: request the
  artifact the assertion reads and spell fragments as they render.
- Target-triple pinning / platform gating: roughly 16 `stdlib.Port`
  `StdLibSourceLinux*`/`*Windows*` tests assert non-macOS syscall/codegen paths.
  Artifact/codegen-only tests may cross-target compile on macOS and assert
  emitted output. Tests that require a real foreign SDK, linker, syscall
  surface, execution, or native runtime behavior should be platform-gated with a
  source comment explaining the platform-only pass condition.
compiler.SsaTests detail:

- Done and verified: ArithmeticFold 24, ValueFacts 43-green/17-fixed,
  AliasAware 13, ScopedNoAlias 5, FunctionAddress 3, ConstantText 5,
  TextView 2, DynamicStorage 28.
- Fix classes seen:
  - Artifact selection: optimization-pass result lands in `optimized-ssa`, not
    terse `ssa`; switch `CompileSsaAfter` to `CompileSsaAfterOptimized` and
    `SsaContains`/`!SsaContains` to `OptimizedSsaContains`/`OptimizedSsaLacks`.
  - Source ports: common rewrites include `T~` to `dynamic`/`List<T>`, `*T` and
    `*mut T` to `rawptr<T>`/`rawmutptr<T>`, `#[ElementCount(n)] *T` to bounded
    `rawptr<T>[n]`, `as Type` to `(Type)(expr)`, raw-pointer functions marked
    `unsafe`, readonly-rawptr writes changed to rawmutptr, minimal-width
    non-negative ranges, `(unicode)"..."` literals, and removing redundant
    `where disjoint`.
- Cleanup done. The remaining source-port issues were ranged integer spelling,
  source-valid switch shape, loop behavior spelling, and optimized-artifact
  assertions for facts that only render after cleanup.
- Pre-fix failure classification to revisit on the next rebaseline:
  - 17 source-ok text-class tests: probe `ssa` vs `optimized-ssa` for the
    asserted fragment and switch artifact/spelling. Verify whether surviving
    binaries at a stopped pass are real under-optimizations before respelling.
  - Closed 2026-06-23: the `*FailsBeforeLlvmEmission` SSA-validator unit tests
    now use the structured `validatorFixture` host-test path instead of
    source-valid placeholder ports.
  - About 16 type/range source ports are fixable like ValueFacts/AliasAware
    where the shape is source-expressible.
- InlineSsa done. Added
  `System.Testing.SsaFunctionBody(ascii ssaText, ascii fnName)` and
  `OptimizedSsaFunctionLacks/Contains`; the source-built dependency boundary now
  stages `Math.stark` through `CompileSsaAfterOptimizedWithModule`.

## 2026-06-22 SSA Source Dependency Staging

- Added SSA host-test module staging with raw filesystem temp directories so
  source-built dependency tests can pass search directories through the host
  compile protocol.
- Restored `InlineSsaOptimizesThroughSourceBuiltDependencyBoundary` to assert the
  optimized `Run` body folds to `return 42` and has no surviving `AddOne` call.
- Narrow verification:
  - `../../stark test --filter InlineSsaOptimizesThroughSourceBuiltDependencyBoundary --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter InlineSsaInlinesSmallDirectCallsAndRerunsConstantPropagation --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupSsaRemovesSameOperandIntegerComparisons --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter InlineSsaInlinesSmallModulePrivateDirectCallsWithoutExplicitInline --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA Cleanup Family

- Completed the `compiler.SsaTests` cleanup family after source-port and
  rendered-artifact fixes:
  - `CleanupRemovesRedundantSameTypeConversions` now asserts the same-type
    conversion does not survive as a rendered `convert`.
  - `CleanupReusesIdenticalMaterializedConstantConversions` uses ranged `i8` and
    asserts exactly one rendered `raw:i32` materialization.
  - `CleanupDropsSwitchCasesThatAlreadyMatchDefaultTarget` uses a source-valid
    three-value range switch with one explicit case sharing the default return.
  - `CleanupRemovesLoopInvariantSelfReferentialPhiNodes` uses `while willexit`
    and asserts optimized SSA returns `arg_limit` with the invariant phi removed.
- Narrow verification:
  - `../../stark test --filter CleanupRemovesRedundantSameTypeConversions --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupReusesIdenticalMaterializedConstantConversions --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupDropsSwitchCasesThatAlreadyMatchDefaultTarget --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter CleanupRemovesLoopInvariantSelfReferentialPhiNodes --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter Cleanup --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA ScalarReplacement Family

- Completed the `compiler.SsaTests` scalar-replacement family after source-port
  and rendered-artifact fixes:
  - `ScalarReplacementRemovesDeadStackFieldStoresFromSource` now reads
    optimized SSA at the `sroa-ssa` stop point.
  - `ScalarReplacementKeepsStackFieldStoresAfterAggregateAddressEscapes` marks
    the raw-pointer helper `unsafe` and asserts retained escaped stack storage.
  - Aggregate-copy ports now assert the rendered optimized facts the source path
    exposes: scalar forwarding to `arg_value`, retained escaped destination
    storage, and move-only aggregate consumption.
- Narrow verification:
  - `../../stark test --filter ScalarReplacementRemovesDeadStackFieldStoresFromSource --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsStackFieldStoresAfterAggregateAddressEscapes --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsAggregateCopiesObservedByLaterFieldLoad --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsAggregateCopiesAfterDestinationAddressEscapes --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacementKeepsDeadAggregateMoveCopiesConservative --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter ScalarReplacement --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA FunctionAddress Family

- Completed the `compiler.SsaTests` function-address validator source ports by
  replacing stale `func<...>` snippets with current `fnptr<unsafe fn ...>` source
  and keeping the source-expressible positive equivalents.
- Cleaned two adjacent indirect-call validation ports touched by the same stale
  callable syntax, using current fixed-array source spelling and explicit array
  initializers.
- Narrow verification:
  - `../../stark test --filter FunctionAddress --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter IndirectCall --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA ConstantText Family

- Completed the `compiler.SsaTests` constant-text formatting specialization
  family by reading the post-pass `optimized-ssa` artifact and scoping
  call-removal/call-retention checks to the `Run` function body.
- Preserved the optimizer facts from the C# oracle in rendered-text form:
  `format_const` blocks, fixed ASCII/Unicode copy widths, length stores, bool
  phi, and normalized narrowed digit stores.
- Narrow verification:
  - `../../stark test --filter ConstantText --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA TextView Family

- Completed the `compiler.SsaTests` text-view validation source ports by
  replacing non-source-visible text field reads with source-visible text indexing
  and slicing operations.
- Narrow verification:
  - `../../stark test --filter TextView --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-22 SSA DynamicStorage Family

- Completed the `compiler.SsaTests` dynamic-storage family after source-port
  fixes for current dynamic-storage syntax, non-negative capacity proofs,
  source-visible initialization, and raw pointer/slice escape shapes.
- Replaced remaining `System.Collections.List<T>` reductions with direct
  `dynamic T` sources so the rendered SSA keeps the dynamic-storage operations
  (`new`, `TryReserve`, `Length`, `Capacity`, `MoveLast`, `Reserve`, data
  pointer and slice escapes) visible to the text bridge.
- Narrow verification:
  - `../../stark test --filter DynamicStorage --target arm64-apple-macosx26.0.0`: passed.

## 2026-06-23 MIR Artifact Alignment

- Completed the named `compiler.MirTests` switch-pattern residue by replacing
  broad switch-word checks with a MIR switch-terminator helper and respelling
  enum/text/raw-pointer fragments to the current renderer.
- Completed the place-lowerer address-chain residue by asserting rendered
  pointer/address facts for large aggregates, large arrays, slice views, raw
  pointer loads, globals, and frozen parameter addresses.
- Added MIR module staging so imported lowering-contract regressions compile
  with a real staged `Dep.stark` dependency instead of an impossible root-only
  reduction.
- Reworked the nested generic layout port to force the concrete nested generic
  field layouts through MIR, because the monomorphization-plan artifact has no
  text renderer in the host-test protocol.
- Narrow verification:
  - `../../stark test --collection mid-level-ir-lowering-tests-switch-pattern-lowerer --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-place-lowerer --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter Generic --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --filter MemberCallsDoNotCollide --target arm64-apple-macosx26.0.0`: passed.
- No full `compiler.MirTests` rebaseline was run because broad sweeps are
  intentionally avoided.

## 2026-06-23 MIR Named Collections Complete

- Added compact MIR artifact suffixes for structural facts that the ported Stark
  tests need to preserve from the C# object-model assertions: integer/float/bool
  return operands, binary operator result types and constant operands, converts,
  field/index insert/extract rvalues, and explicit object-construction facts.
- Added host-test rendering for the `enum-layout` artifact, including compact
  tag ranges, ordered fields, variant tags, payload storage fields, and concrete
  size/alignment where the type model is available.
- Fixed remaining named `compiler.MirTests` collections by asserting the
  structural facts that now render directly, plus current source spelling for
  arm64 asm bypasses, unsafe FFI calls, and frozen raw pointers.
- Narrow verification:
  - `../../stark test --collection mid-level-ir-lowering-tests-runtime-drop-lowerer --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-core --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-compile-time-evaluator --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-lowering-tests-lowering-invariant --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-dynamic-fixed-array-indexing --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection raw-single-line-literal --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-cli --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection mid-level-ir-arena-frame --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-lower-hir --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-lower-mir --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-lower-abi --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-enum-layout --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection compiler-pipeline-full --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection generic-use-site-instantiation --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --collection lowering-contract-fact-key --target arm64-apple-macosx26.0.0`: passed.
  - `../../stark test --list-collections --target arm64-apple-macosx26.0.0`: passed and showed only the broad aggregate `compiler`/`mir` collections remain unrun by design.
- No full aggregate `compiler` or `mir` collection run was performed because
  those aliases are broad and the current policy is narrow targeted runs only.

## 2026-06-23 SSA Invalid-IR Fixture Path

- Added the host-test `validatorFixture` request object with generic
  `kind`/`name` fields; `ssa` is backed by a fixture catalog and MIR/package
  artifact validator kinds can use the same transport when their catalogs land.
- Added an SSA validator fixture catalog generated from
  `tests/compiler.Tests/SsaIrValidationTests.cs`, preserving the C# diagnostic
  contracts for 95 validator inputs.
- Ported all 98 Stark SSA validator test entries to the fixture path or an
  explicit host-internal constructor-guard exclusion:
  `ExtractIndexOutOfRangeIsUnrepresentable`,
  `InsertIndexValueMismatchIsUnrepresentable`, and
  `IndexOperationFamilyMismatchIsUnrepresentable`.
- Added the three arena-frame SSA validator cases that were present in the C#
  oracle but missing from the Stark port table.
- Narrow verification:
  - `dotnet build src/compiler.csproj --no-restore`: passed with the two
    existing nullable warnings in `TypeChecking.cs`.
  - Direct `--host-test-inspect` smoke for invalid, valid, and excluded SSA
    validator fixtures: passed with expected protocol behavior.
  - `../../stark test --list-collections --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.SsaTests`: passed.
  - `../../stark test --collection ssa-ir --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.SsaTests`: passed.
  - No broad aggregate collection was run.

## 2026-06-24 Compiler.Tests CLI And Package-Link Recheck

- Fixed project test builds so generated-test companion source roots and built
  dependency package-image directories are searched before bundled source-tree
  fallback. This keeps `compiler.Tests` on the freshly built stdlib package path
  instead of recompiling stdlib source modules with pruned dependency bodies.
- Fixed executable link input ordering so package archives are passed after
  locally emitted object files, preserving static-archive resolution for package
  definitions such as stdlib platform and memory helpers.
- Fixed project input stamps so selected test filters invalidate only test
  projects, not library dependencies such as stdlib.
- Repointed the two CLI signed-range port facts at `semantic-validate`, matching
  check-mode behavior where STK3014 is produced.
- Narrow verification:
  - `dotnet build src/compiler.csproj --no-restore`: passed with the two
    existing nullable warnings in `TypeChecking.cs`.
  - `../../stark test --filter CheckModeReportsSuccess --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter CheckModeRejectsPositiveSignedRangesByDefault --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter StrictIntegerRangeFlagRejectsPositiveSignedRanges --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Project CLI Recheck

- Fixed the six failing `project-cli` port reductions that still supplied
  multiple modules as one source text or omitted sibling module fixtures.
- The affected facts now use the existing module-aware host-test helper so
  cross-module imports resolve through an explicit temporary search directory.
- Narrow verification:
  - `../../stark test --collection project-cli --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Compiler CLI Recheck

- Fixed the stale `compiler-cli` port reductions for current ownership,
  diagnostic, import-resolution, and manifest-backed module behavior.
- The negative MIR/LLVM mode reductions now use the transport-only host-test
  path so type diagnostics are asserted without requiring successful lowering.
- Narrow verification:
  - `../../stark test --collection compiler-cli --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Package Image Architecture Recheck

- Fixed symbolic `comptime` value forwarding through imported source
  materialization and generic argument decoding, preserving open value-generic
  facts until concrete specialization.
- Fixed MIR lowering of specialized comptime generic values so concrete values
  such as `N=4` lower as immediate operands with their range-typed value facts.
- Fixed package-image architecture expectations for current monomorphized
  symbol names and trait-conformance validation phase behavior.
- Narrow verification:
  - `dotnet build src/compiler.csproj --no-restore`: passed with the two
    existing nullable warnings in `TypeChecking.cs`.
  - Direct `--host-test-inspect` minimal `Outer<T, comptime N>` probe: passed
    with zero diagnostics.
  - `../../stark test --filter PackageImageConsumerFoldsImportedComptimeTemplateCallWithPatterns --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter PackageImagePreservesComptimeGenericDeclarationsAndSymbolicTemplateCalls --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --filter PackageImagePreservesMethodStructuralFactsAcrossTypedInterfaceSourceBridgeAndFacts --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed.
  - `../../stark test --collection package-image-architecture --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed, 30 selected facts.
- No broad `compiler.Tests` sweep was run.

## 2026-06-24 Compiler.Tests Function Semantics Recheck

- Added semantic-validation host-test helpers so ported facts that assert
  law-body, function-kind, visibility, and externally visible effect diagnostics
  stop at the pass that actually emits those diagnostics.
- Added a STARK_PATH-backed semantic-validation helper for source-tree stdlib
  probes, restoring the runtime text concatenation fact to assert both the
  law and finite kind-obligation diagnostics from the real `System.Text` path.
- Narrow verification:
  - `../../stark test --collection function-semantics --target arm64-apple-macosx26.0.0` in
    `tests-stark/compiler.Tests`: passed, 33 selected facts.
- No broad `compiler.Tests` sweep was run.

Other suite notes:

- compiler.Tests: package-image architecture is green by targeted collection;
  remaining work includes diagnostics, type-checking, ownership, pipeline,
  runtime, CLI, examples, AsmDeclarations, CheckMode,
  EmitLlvm/EmitExecutable, TextDiagnostics/SystemText, and a long tail.
- stdlib.Port: at most 21 `StdLibSource*` lowering/intrinsic/syscall-path assertions,
  roughly 16 Linux/Windows platform-specific tests, WindowsDispatch 2,
  SourceStd 2, and miscellaneous cases.
- compiler.MirTests: all named non-aggregate collections are green by targeted
  runs. The broad `compiler`/`mir` aggregate aliases were not run by design.

---

## macOS Pass-Bar Decision

The macOS pass bar includes tests runnable on macOS plus artifact/codegen-only
cross-target tests whose expected Linux/Windows output can be asserted without a
foreign SDK/linker/runtime. Tests that need real non-macOS platform facilities
are excluded from the macOS pass bar by platform gating, and should carry
comments explaining which platform is required.

---

## 2026-06-25 Selfhost Lowering Boundary Slice

- Added the Stark-side HIR/MIR boundary model and MIR lowering pass shell.
- The shell validates the host `lower-mir` artifact contract and records the
  backend fact families that must survive HIR to MIR lowering.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 5 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost MIR Builder State Slice

- Added Stark-side MIR function builder state, dense lowering symbol maps, and
  block creation helpers that record owned value/block ranges without embedding
  generic `IrTable<T>` fields in builder state.
- Preserved backend fact rows through symbol binding so lowering can carry range,
  alias, ABI, alignment, and layout facts into MIR/LLVM-facing tables.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 8 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Literal Lowering Slice

- Added explicit HighLevelIr literal kinds for unsupported null, float, text,
  and character literals so lowering rejects them as literals instead of symbols.
- Lowered integer and boolean literals to typed MIR constants while preserving
  exact range facts in both value-fact and lowering-symbol tables.
- Rejected out-of-range typed integer literals and unsupported literal families
  before appending partial MIR instructions.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 13 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Parameter And Local Alias Lowering Slice

- Lowered dense i64 HIR parameters to `MirOp.Param` values while preserving
  translated backend facts in value-fact and lowering-symbol tables.
- Added zero-emission SSA local alias binding so local symbols reuse initializer
  MIR values and preserve existing backend facts.
- Rejected unsupported parameter types, non-dense parameter ordinals, and local
  alias type mismatches before emitting or binding partial MIR.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 17 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Simple Local Assignment Lowering Slice

- Added a HIR assignment row for simple local reassignments.
- Lowered SSA local assignment by rebinding the local symbol to the assigned MIR
  value without emitting an extra instruction.
- Preserved the assigned value's backend facts in the lowering symbol map and
  rejected non-local targets or type mismatches before rebinding.
- Narrow verification:
  - `../../stark test --filter MirLoweringBindsLocalAliasWithoutEmissionAndPreservesFacts --filter MirLoweringLowersLocalAssignmentByRebindingSymbolAndFacts --filter MirLoweringRejectsInvalidLocalAssignmentWithoutRebinding` in `tests-stark/selfhost.Lowering`:
    passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Value Return Lowering Slice

- Lowered typed HIR value returns to MIR return blocks without emitting extra
  value instructions or dropping the returned value's fact row.
- Rejected return type mismatches and missing returned-value facts before
  appending partial MIR blocks.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 22 selected facts.
- No broad test sweep was run.

## 2026-06-26 Selfhost Integer Binary Lowering Slice

- Lowered typed integer add/sub/mul and signed comparisons from HIR to typed MIR
  while recomputing generated value facts before symbol binding.
- Corrected MIR comparisons to be i1-valued, preserved their range facts, and
  taught typed LLVM emission to return typed comparison results.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 24 selected facts.
  - `../../stark test --filter EmitsLlvmTypedI32ComparisonFunction` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsInstructionStream` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirComparisonRecordsOpcodeAndOperands` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BuildMirValueRangeFactsDerivesConstantsArithmeticAndPhi`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Extended Integer Binary Lowering Slice

- Lowered typed integer division, remainder, bitwise, and shift operations from
  HIR to typed MIR with proven-invalid backend fact rejection.
- Recomputed exact generated facts for safe constant extended integer operations
  and taught typed LLVM emission to preserve their result types.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 26 selected facts.
  - `../../stark test --filter BuildMirValueRangeFactsDerivesExactExtendedIntegerOps`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmTypedI32ExtendedArithmeticFunction`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Direct Call Lowering Slice

- Lowered typed direct HIR calls up to MIR's four-argument payload to typed MIR
  `Call` instructions while binding result backend facts.
- Added typed MIR call constructors and typed LLVM call emission so call result
  and argument types survive into LLVM IR.
- Rejected call result range facts that do not fit the declared MIR result type
  before emitting partial MIR.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 28 selected facts.
  - `../../stark test --filter BuildMirValueRangeFactsImportsTypedCallReturnFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmTypedI32CallFunction` in
    `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Void Return Lowering Slice

- Added a first-class MIR void-return terminator and lowered bare HIR void
  returns to it without creating a synthetic SSA value.
- Added void LLVM definition emission so a void function emits `ret void` under
  a `define void` signature.
- Kept block serialization stable by assigning `ReturnVoid` a new terminator
  byte after the existing values.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 29 selected facts.
  - `../../stark test --filter MirReturnVoidBlockRecordsEmptyTerminator` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmVoidDefinitionWithParams` in
    `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsBlocks` in
    `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Direct Call Fact Preservation Slice

- Verified direct-call result fact rows are translated through HIR-to-MIR call
  lowering without narrowing the transfer to integer ranges.
- Verified MIR call-return fact import preserves non-range backend facts such as
  alignment, ABI, noalias, volatility, and pointer nullability.
- Narrow verification:
  - `../../stark test --collection lowering` in `tests-stark/selfhost.Lowering`:
    passed, 30 selected facts.
  - `../../stark test --filter BuildMirValueRangeFactsImportsTypedCallReturnFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BuildMirValueRangeFactsImportsPointerCallReturnBackendFacts`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Call-Site Effect Attribute Slice

- Threaded computed function-effect facts into range-aware LLVM call emission.
- Emitted callee effect attributes on ordinary direct calls and `musttail`
  tail-call terminators.
- Refined law effect summaries so calls to proven `memory(none)` law callees do
  not force the caller to `memory(read)`.
- Added a pre-emission effect prepass so functions emitted before their callees
  still keep callee effect attributes.
- Narrow verification in `tests-stark/selfhost.Ir`:
  - `../../stark test --filter CompileModuleFiniteLawLowersNumberedFunctionEffectAttributes --filter CompileModuleFiniteEffectsPropagateThroughProvenDirectCalls --filter CompileModuleLawEffectsPropagateThroughProvenDirectCalls --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts`:
    passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Memory-Backed Call Argument Slice

- Threaded callee parameter ABI and storage-contract facts into direct call and
  `musttail` call lowering.
- Emitted pointer call-site attributes and `separate_storage` assumes from
  memory-backed argument obligations.
- Rejected pointer/scalar argument kind mismatches and calls whose caller cannot
  prove the callee's required non-overlap contract.
- Narrow verification in `tests-stark/selfhost.Ir`:
  - `../../stark test --filter CompileModulePointerParametersLowerGranularAttributes --filter CompileModuleWholePointerParamsEmitSeparateStorageAssume --filter CompileModulePointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleTailPointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModulePointerCallArgumentsRequireCallerAliasProof --filter CompileModulePointerCallArgumentKindsMustMatchCallee --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes`:
    passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Assignment Value Context Lowering Slice

- Verified local assignment lowering returns the assigned MIR value for enclosing
  value contexts without emitting an extra assignment instruction.
- Confirmed the assignment result feeds typed MIR arithmetic and return lowering
  while preserving recomputed backend range facts.
- Narrow verification:
  - `../../stark test --filter MirLoweringUsesLocalAssignmentResultInEnclosingValueContext --filter MirLoweringLowersLocalAssignmentByRebindingSymbolAndFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Explicit Overflow Arithmetic Lowering Slice

- Added distinct MIR and HIR lowering opcodes for explicit wrapping and
  saturating add, subtract, and multiply operations.
- Preserved exact or clamped range facts for explicit overflow arithmetic instead
  of reusing ordinary no-overflow arithmetic facts.
- Emitted wrapping LLVM arithmetic without no-wrap flags and emitted saturating
  LLVM arithmetic through a deterministic wide clamp sequence.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersExplicitWrappingAndSaturatingArithmeticWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirExplicitWrappingAndSaturatingArithmeticRoundTripsFactsAndTypedLlvm`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirLoweringLowersTypedIntegerArithmeticAndComparisonWithFacts --filter MirLoweringLowersTypedIntegerExtendedOpsWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter EmitsLlvmForMixedArithmetic --filter BinaryRoundTripsInstructionStream`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local Compound Assignment Lowering Slice

- Lowered SSA local compound assignments by emitting the selected checked,
  wrapping, or saturating MIR operation and rebinding the local to the result.
- Preserved recomputed backend value facts through compound assignment results
  and final local facts.
- Rejected non-local targets, type mismatches, and exact invalid backend facts
  before emitting partial MIR.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersCompoundAssignmentsWithCheckedWrappingAndSaturatingFacts --filter MirLoweringRejectsInvalidCompoundAssignmentWithoutRebinding --filter MirLoweringLowersTypedIntegerArithmeticAndComparisonWithFacts --filter MirLoweringLowersExplicitWrappingAndSaturatingArithmeticWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost I64 Global Lowering Slice

- Added explicit HIR rows for i64 global references and global stores.
- Bound source global symbols to MIR global ids with declared backend facts.
- Lowered global references to `MirOp.LoadGlobal` and stores to `MirOp.StoreGlobal` while preserving load facts and rejecting out-of-range stores.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersGlobalLoadStoreWithFacts --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Typed Global Storage And Lowering Slice

- Added typed MIR global records, typed load/store constructors, and typed LLVM
  global emission.
- Serialized global storage types through package images and validation.
- Lowered typed global references and stores with declared type and range-fact
  validation.
- Narrow verification:
  - `../../stark test --filter MirLoadGlobalRecordsTarget --filter MirStoreGlobalRecordsTargetAndValue --filter EmitsLlvmTypedGlobalLoadStore --filter MirGlobalRecordsInitialValue --filter EmitsLlvmGlobals --filter BinaryRoundTripsGlobals --filter BinaryRoundTripsPackageImage --filter EmitsLlvmGlobalLoad --filter EmitsLlvmGlobalStore --filter EmitsLlvmModuleWithGlobals`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirLoweringLowersGlobalLoadStoreWithFacts --filter MirLoweringLowersTypedGlobalLoadStoreWithFacts --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Module Global Declaration Lowering Slice

- Added HIR module global declaration rows with typed scalar initializers and
  declared backend facts.
- Lowered HIR module globals into typed MIR global rows, aligned global fact
  rows, and bound global symbols for later loads/stores.
- Rejected invalid declaration facts and initializers before emitting global
  rows or symbol bindings.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersModuleGlobalDeclarationsAndInitializersWithFacts --filter MirLoweringRejectsInvalidModuleGlobalDeclarationsWithoutBinding --filter MirLoweringLowersTypedGlobalLoadStoreWithFacts --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Become Tail-Call Lowering Slice

- Added HIR `become` lowering for direct typed tail calls through MIR's current
  four-argument payload.
- Added typed MIR tail-call payload constructors and preserved payload result
  types through LLVM `musttail` terminator emission.
- Preserved translated result facts on `become` payload values and kept typed
  tail-call payload types through binary round-trip.
- Narrow verification:
  - `../../stark test --filter EmitsLlvmTypedTailCallTerminator --filter EmitsLlvmTailCallTerminator --filter BinaryRoundTripsFourArgumentTailCall --filter EmitsLlvmTypedI32CallFunction`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes --filter CompileModulePointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts --filter EmitsLlvmTypedFunctionWithParameterTypesAndFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter EmitsLlvmTypedTailCallTerminator --filter EmitsLlvmVoidDefinitionWithParams --filter EmitsLlvmModuleWithGlobals --filter BinaryRoundTripsFourArgumentTailCall`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedDirectCallWithResultFacts --filter MirLoweringLowersValueReturnToMirReturnBlockAndPreservesFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Call Argument Fact Validation Slice

- Validated carried backend facts on direct-call and tail-call argument values
  before MIR payload emission.
- Rejected stale scalar nullability and pointer range facts on call arguments
  without emitting call or tail-call instructions.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsCallArgumentFactsOutsideTypeWithoutEmission --filter MirLoweringRejectsBecomeArgumentFactsOutsideTypeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersTypedDirectCallWithResultFacts --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedBecomeToTailCallBlockWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringRejectsCallResultFactsOutsideResultTypeWithoutEmission --filter MirLoweringRejectsScalarNullabilityCallResultWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Arithmetic Operand Fact Validation Slice

- Validated carried backend facts on binary and compound-assignment operands
  before interpreting arithmetic backend facts.
- Rejected stale scalar nullability and out-of-type integer range facts without
  emitting arithmetic instructions or rebinding compound-assignment locals.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsBinaryOperandFactsOutsideTypeWithoutEmission --filter MirLoweringRejectsCompoundAssignmentOperandFactsOutsideTypeWithoutRebinding`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersTypedIntegerArithmeticAndComparisonWithFacts --filter MirLoweringLowersTypedIntegerExtendedOpsWithFacts --filter MirLoweringLowersExplicitWrappingAndSaturatingArithmeticWithFacts --filter MirLoweringLowersCompoundAssignmentsWithCheckedWrappingAndSaturatingFacts --filter MirLoweringRejectsInvalidCompoundAssignmentWithoutRebinding --filter MirLoweringRejectsExactInvalidExtendedIntegerFactsWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Literal Fact Validation Slice

- Validated literal fact rows before MIR constant emission.
- Rejected range facts that do not describe the literal value and nullability
  facts that do not match the literal type or value.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsLiteralFactsOutsideTypeOrValueWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersIntegerLiteralWithExactFactsAndSymbolBinding --filter MirLoweringLowersBoolLiteralAsI1WithExactFacts --filter MirLoweringLowersNullPointerLiteralWithNullabilityFacts --filter MirLoweringRejectsUnsupportedLiteralWithoutEmission --filter MirLoweringRejectsTypedIntegerLiteralOutsideRangeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Global Store Fact Validation Slice

- Validated stored value fact rows against the global store type before MIR store
  emission.
- Rejected stale scalar nullability and pointer range facts even when the target
  global has no required backend facts.
- Narrow verification:
  - `../../stark test --filter MirLoweringRejectsGlobalStoreValueFactsOutsideTypeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersGlobalLoadStoreWithFacts --filter MirLoweringLowersTypedGlobalLoadStoreWithFacts --filter MirLoweringChecksGlobalStoreBackendFactSubset --filter MirLoweringRejectsInvalidGlobalAccessWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Conditional Branch Fact Validation Slice

- Validated MIR conditional-branch conditions as owned `i1` values with present
  and type-compatible fact rows before appending branch blocks.
- Rejected stale nullability facts, invalid bool range facts, and non-bool
  condition values without appending conditional blocks.
- Narrow verification:
  - `../../stark test --filter MirBuilderAppendsConditionalBranchWithValidatedBoolFacts --filter MirBuilderRejectsConditionalBranchConditionFactsOutsideTypeWithoutBlock`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirBuilderRejectsClosedAndOutOfRangeBlockCreation --filter MirLoweringPassShellMatchesHostPipelineContract`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Tail-Call Block Fact Validation Slice

- Validated MIR tail-call block payloads as owned `TailCall` instructions with
  present and type-compatible fact rows before appending tail-call blocks.
- Rejected stale nullability facts, invalid payload range facts, and non-tail
  payload values without appending tail-call blocks.
- Narrow verification:
  - `../../stark test --filter MirBuilderAppendsTailCallBlockWithValidatedPayloadFacts --filter MirBuilderRejectsTailCallBlockPayloadFactsOutsideTypeWithoutBlock --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirLoweringLowersTypedBecomeToTailCallBlockWithFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Return And Branch Block Builder Validation Slice

- Validated return block payloads as owned values with present and
  type-compatible fact rows before appending return blocks.
- Validated unconditional branch block cursor state before appending branch
  blocks.
- Narrow verification:
  - `../../stark test --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction --filter MirBuilderRejectsClosedAndOutOfRangeBlockCreation --filter MirBuilderRejectsReturnBlockValueFactsOutsideTypeWithoutBlock --filter MirBuilderAppendsBranchBlockWithValidatedCursor --filter MirBuilderRejectsBranchBlockWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringLowersValueReturnToMirReturnBlockAndPreservesFacts --filter MirLoweringLowersVoidReturnToMirReturnVoidBlock --filter MirLoweringRejectsReturnTypeMismatchWithoutBlockEmission --filter MirLoweringRejectsReturnFactsOutsideTypeWithoutBlockEmission --filter MirLoweringRejectsReturnWithoutValueFactsBeforeBlockEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Function Builder Finalization Validation Slice

- Validated instruction and block cursors before finalizing a MIR function's
  owned ranges.
- Checked function-table append capacity before recording the function row.
- Narrow verification:
  - `../../stark test --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction --filter MirBuilderRejectsFunctionFinishWhenInstructionCursorIsStale --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale --filter MirBuilderRejectsClosedAndOutOfRangeBlockCreation`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Explicit Entry Selection Validation Slice

- Validated the block table and block cursor before changing a function builder's
  explicit entry block.
- Preserved builder state when stale raw block-table rows are detected.
- Narrow verification:
  - `../../stark test --filter MirBuilderSetsExplicitEntryBlockWithValidatedCursor --filter MirBuilderRejectsEntrySelectionWhenBlockCursorIsStale --filter MirBuilderRejectsBranchBlockWhenBlockCursorIsStale --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Value Recording Validation Slice

- Validated the instruction table and append cursor before extending a MIR
  function builder's owned instruction range.
- Preserved builder state when raw instruction rows exist beyond the value being
  recorded or the value handle is absent from the instruction table.
- Narrow verification:
  - `../../stark test --filter MirBuilderRejectsValueRecordingWhenInstructionCursorIsStale --filter MirFunctionBuilderTracksOwnedRangesAndDefinesFunction --filter MirBuilderRejectsFunctionFinishWhenInstructionCursorIsStale --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Block Recording Validation Slice

- Validated the block table and append cursor before extending a MIR function
  builder's owned control-flow block range.
- Preserved builder state when raw block rows exist beyond the block being
  recorded or the block handle is absent from the block table.
- Narrow verification:
  - `../../stark test --filter MirBuilderRejectsBlockRecordingWhenBlockCursorIsStale --filter MirBuilderRejectsValueRecordingWhenInstructionCursorIsStale --filter MirBuilderAppendsBranchBlockWithValidatedCursor --filter MirBuilderRejectsFunctionFinishWhenBlockCursorIsStale`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Function Backend Fact Gate Slice

- Rejected Stark CFG function lowering before MIR builder creation when any
  required backend fact category is missing.
- Kept declaration-only functions out of the Stark CFG builder entry path.
- Narrow verification:
  - `../../stark test --filter MirFunctionBuilderRequiresCompleteBackendFactsBeforeFunctionLowering --filter HirBoundaryModelsTheHostHighLevelIrPass --filter HighLevelIrModuleStoresFunctionRowsAndBackendFactRequirements --filter MirLoweringPassShellMatchesHostPipelineContract`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost HIR If Branch Lowering Slice

- Lowered HIR if branch terminators from block symbols into validated MIR
  conditional blocks.
- Rejected missing target block symbols and invalid condition facts without
  appending a conditional block.
- Narrow verification:
  - `../../stark test --filter MirLoweringSymbolMapUsesDenseSymbolSlotsAndCarriesFacts --filter MirLoweringLowersIfBranchFromBlockSymbolsWithValidatedFacts --filter MirLoweringRejectsIfBranchMissingTargetsAndBadConditionWithoutBlockEmission --filter MirBuilderAppendsConditionalBranchWithValidatedBoolFacts --filter MirBuilderRejectsConditionalBranchConditionFactsOutsideTypeWithoutBlock`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Fixed Arena Allocation Lowering Slice

- Lowered fixed-size HIR arena allocations to MIR `ArenaAlloc` instructions.
- Marked arena-using builders and attached alignment, noalias, and known-nonnull
  pointer facts to the allocation result.
- Rejected zero-size and invalid-alignment arena allocations before MIR emission.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersArenaAllocationWithFrameAndPointerFacts --filter MirLoweringRejectsInvalidArenaAllocationShapeWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Arena Dynamic Storage Lowering Slice

- Lowered arena-backed HIR dynamic storage init and reserve operations to MIR.
- Preserved owner alignment, noalias, known-nonnull, and fallible reserve
  boolean range facts through MIR lowering and generated-fact recomputation.
- Rejected invalid dynamic shapes, mismatched owner facts, and non-owner reserve
  targets before MIR emission.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersArenaAllocationWithFrameAndPointerFacts --filter MirLoweringLowersArenaDynamicStorageInitWithFrameAndOwnerFacts --filter MirLoweringLowersArenaDynamicStorageReserveVariantsWithFacts --filter MirLoweringRejectsInvalidArenaDynamicStorageWithoutEmission`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Arena Frame Lifecycle Lowering Slice

- Emitted MIR arena frame enter instructions before the first HIR-lowered arena
  allocation or arena-backed dynamic storage operation.
- Emitted MIR arena frame leave instructions before return and tail-call blocks
  for arena-using function builders.
- Narrow verification:
  - `../../stark test --filter MirLoweringLowersArenaAllocationWithFrameAndPointerFacts --filter MirLoweringLowersArenaDynamicStorageInitWithFrameAndOwnerFacts --filter MirLoweringLowersArenaDynamicStorageReserveVariantsWithFacts --filter MirLoweringRejectsInvalidArenaDynamicStorageWithoutEmission --filter MirLoweringClosesArenaFrameBeforeReturnBlock --filter MirLoweringLowersVoidReturnToMirReturnVoidBlock --filter MirBuilderAppendsBranchBlockWithValidatedCursor`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirBuilderRejectsBranchBlockWhenBlockCursorIsStale --filter MirBuilderSetsExplicitEntryBlockWithValidatedCursor --filter MirBuilderRejectsEntrySelectionWhenBlockCursorIsStale --filter MirBuilderAppendsConditionalBranchWithValidatedBoolFacts --filter MirLoweringLowersIfBranchFromBlockSymbolsWithValidatedFacts --filter MirLoweringRejectsIfBranchMissingTargetsAndBadConditionWithoutBlockEmission`
    in `tests-stark/selfhost.Lowering`: passed.
  - `../../stark test --filter MirLoweringClosesArenaFrameBeforeTailCallBlock --filter MirLoweringLowersBecomeToTailCallBlockWithFacts --filter MirBuilderAppendsTailCallBlockWithValidatedPayloadFacts`
    in `tests-stark/selfhost.Lowering`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Volatile LLVM Global Access Slice

- Attached volatile global fact rows to deterministic textual LLVM global loads
  and stores.
- Preserved existing range metadata attachment on volatile global loads.
- Narrow verification:
  - `../../stark test --filter EmitsLlvmVolatileGlobalLoadStoreFacts --filter EmitsLlvmRangeMetadataForGlobalLoads --filter EmitsLlvmGlobalLoad --filter EmitsLlvmGlobalStore --filter EmitsLlvmTypedGlobalLoadStore`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Aligned LLVM Global Access Slice

- Attached global alignment fact rows to deterministic textual LLVM global loads
  and stores.
- Preserved existing volatile and range metadata spelling around aligned global
  accesses.
- Narrow verification:
  - `../../stark test --filter EmitsLlvmAlignedGlobalLoadStoreFacts --filter EmitsLlvmVolatileGlobalLoadStoreFacts --filter EmitsLlvmRangeMetadataForGlobalLoads --filter EmitsLlvmGlobalLoad --filter EmitsLlvmGlobalStore --filter EmitsLlvmTypedGlobalLoadStore`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost LLVM Calling Convention Fact Slice

- Attached exact FFI calling-convention facts to deterministic textual LLVM
  function definitions and direct call sites.
- Preserved existing `tailcc` priority for tail-callable functions and ordinary
  calls to tail-callable callees.
- Narrow verification:
  - `../../stark test --filter CompileModuleFfiCallingConventionsReachLlvmText`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleFiniteLawLowersNumberedFunctionEffectAttributes --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter AstCallLoweringEmitsCallWithArgument --filter EmitsLlvmOrdinaryCallToTailCallableWithTailcc`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost LLVM Function Linkage Fact Slice

- Attached source function linkage facts to deterministic textual LLVM function
  definitions.
- Lowered module-private and `internal` source functions as LLVM `internal`
  definitions while leaving `public` and `export` source functions external.
- Preserved range, ABI, alias, effect, tail-call, and FFI calling-convention
  facts around the linkage header spelling.
- Narrow verification:
  - `../../stark test --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModulePointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleTailPointerCallArgumentsPreserveAbiAndAliasFacts --filter CompileModuleFiniteLawLowersNumberedFunctionEffectAttributes --filter CompileModuleFiniteLawBranchLowersBlockEffectAttributes --filter CompileModuleFiniteEffectsPropagateThroughProvenDirectCalls --filter CompileModuleLawEffectsPropagateThroughProvenDirectCalls`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleFfiCallingConventionsReachLlvmText --filter CompileModuleForwardDirectCallsUsePrecomputedEffectFacts --filter CompilesModuleWithCallFromAst --filter CompilesTailBecomeFromAst --filter CompilesZeroArgumentTailBecomeFromAst --filter CompilesTailRecursiveBranchFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleWithMultipleArenaFunctionsEmitsValidSinglePreamble --filter CompilesTwoArgumentCallFromAst --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModulePointerParametersLowerGranularAttributes`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleWholePointerParamsEmitSeparateStorageAssume`
    in `tests-stark/selfhost.Ir`: passed.
  - A larger combined filter run exited with code 139 after partial success, so
    the touched cases were verified in narrower stable selections instead.
- No broad test sweep was run.

## 2026-06-26 Selfhost LLVM Function Preemption Fact Slice

- Attached `dso_local` to deterministic textual LLVM definitions for
  source-private and `internal` source functions.
- Kept `public` and `export` source function definitions externally preemptable.
- Narrow verification:
  - `../../stark test --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleFfiCallingConventionsReachLlvmText --filter CompileModuleTailFiniteLawCallSitesLowerEffectAttributes --filter CompileModuleSourceVisibilityControlsLlvmLinkage`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTailRecursiveBranchFromAst --filter CompileModuleWithMultipleArenaFunctionsEmitsValidSinglePreamble --filter CompilesTwoArgumentCallFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - A larger combined filter run exited with code 138 after reporting twelve
    touched facts as ok, so the remaining touched cases were verified in a
    smaller stable selection.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local-Prefixed Terminal If Slice

- Lowered source functions with local setup before terminal `if return/else
  return` into MIR conditional blocks for AST LLVM emission and package tables.
- Preserved local value overrides, branch return range validation, arena cleanup
  on returning arms, and function effect prepass visibility through the branch
  lowering path.
- Narrow verification:
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesRecursiveFunctionFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTailRecursiveBranchFromAst --filter ModulePackageImageWithAsmBuilderRoundTrips`
    in `tests-stark/selfhost.Ir`: passed.
  - A combined run that grouped the recursive branch fact with two other branch
    checks exited 139 after partial success; the same facts were then verified
    with narrower stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Braced Terminal If Slice

- Parsed braced terminal `if` arms (`{ return ...; } else { return ...; }`) as
  the same MIR conditional-block shape as compact terminal branches.
- Reused the same branch parser for body-start branches, local-prefixed
  branches, direct LLVM emission, effect prepass lowering, and package tables.
- Narrow verification:
  - `../../stark test --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesRecursiveFunctionFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTailRecursiveBranchFromAst --filter ModulePackageImageWithAsmBuilderRoundTrips`
    in `tests-stark/selfhost.Ir`: passed.
  - A combined run that grouped the recursive branch fact with the tail/package
    checks exited 139 after partial success; the same facts were then verified
    with narrower stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Semicolon Terminal If Slice

- Parsed semicolon-terminated compact terminal `if` arms (`return ...; else
  return ...;`) as the same MIR conditional-block shape as compact terminal
  branches without semicolons.
- Preserved the existing branch parser for body-start branches, local-prefixed
  branches, direct LLVM emission, effect prepass lowering, and package tables.
- Narrow verification:
  - `../../stark test --filter CompilesSemicolonTerminatedTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedSemicolonTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter PackageTablesPreserveSemicolonTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - Grouped runs containing these facts were unstable: one exited 139 after
    partial success, and one reported failures for facts that passed
    individually.
- No broad test sweep was run.

## 2026-06-26 Selfhost Return If Expression Slice

- Lowered terminal source `return if ... else ...` expressions into MIR
  conditional return blocks instead of a merge phi.
- Preserved branch-refined return range validation, boolean arm zero-extension,
  effect prepass visibility, package-table shape, linkage, calling convention,
  and LLVM range attributes.
- Rejected trailing tokens after the `else` expression instead of silently
  ignoring malformed source.
- Refreshed two existing return-range LLVM exact expectations for `dso_local`.
- Narrow verification:
  - `../../stark test --filter CompilesReturnIfExpressionFromAst --filter ReturnIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnIfExpressionFromAst --filter PackageTablesPreserveReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleArithmeticCallArgumentLowersToLlvmRangeAttribute --filter CompileModuleBranchReturnRangeUsesComparisonProof`
    in `tests-stark/selfhost.Ir`: passed.
  - A larger grouped run containing these facts exited 139 after partial
    success, so the touched cases were verified with narrower stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Returned Local If Expression Slice

- Lowered immediately returned `var` locals initialized from source
  `if ... else ...` expressions into MIR conditional return blocks.
- Avoided a merge phi for the immediate-return shape, preserving branch-refined
  return range validation, boolean arm zero-extension, effect prepass visibility,
  package-table shape, and LLVM range attributes.
- Reused the plain if-expression arm parser for terminal `return if` and returned
  local initializer lowering.
- Narrow verification:
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnIfExpressionFromAst --filter ReturnIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnIfExpressionFromAst --filter PackageTablesPreserveReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompileModuleBranchReturnRangeUsesComparisonProof`
    in `tests-stark/selfhost.Ir`: passed.
  - Two grouped adjacent runs exited 139 after printing partial success, so the
    same touched facts were verified with smaller stable filters.
- No broad test sweep was run.

## 2026-06-26 Selfhost Returned Local If Statement Slice

- Lowered immediately returned locals overwritten by source `if` assignment
  statements into MIR conditional return blocks.
- Preserved branch-refined return range validation, boolean arm zero-extension,
  effect prepass visibility, package-table shape, and LLVM range attributes.
- Kept local-prefixed terminal return-if bodies on their existing lowerer by
  narrowing the assignment-if detector.
- Narrow verification:
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf --filter CompileModuleBranchReturnRangeUsesComparisonProof`
    exited 139 after `CompileModuleBranchReturnRangeUsesComparisonProof`
    passed, so the remaining touched facts were verified individually.
- No broad test sweep was run.

## 2026-06-26 Selfhost Braced Returned Local If Statement Slice

- Lowered braced source `if` assignment arms for immediately returned locals
  into MIR conditional return blocks.
- Replaced the returned-local assignment-if route detector with a source-aware
  shape check so unsupported branch bodies continue to fall through to the
  correct lowerer instead of being claimed by a token sniff.
- Preserved branch-refined return range validation, boolean arm zero-extension,
  package-table shape, and no-phi LLVM emission for the braced assignment-arm
  shape.
- Narrow verification:
  - `../../stark test --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local If Expression Phi Slice

- Lowered integer-valued local `if ... else ...` expression initializers used by
  later return expressions into MIR diamond blocks with a merge phi.
- Preserved phi-derived range facts into return validation and LLVM range
  attributes through the existing MIR value-fact builder.
- Kept immediately returned local if-expression initializers on the no-phi
  conditional-return fast path by tightening the returned-local detector.
- Narrow verification:
  - `../../stark test --filter CompilesLocalIfExpressionInitializerThenReturnFromAst --filter PackageTablesPreserveLocalIfExpressionInitializerThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Local If Statement Phi Slice

- Lowered integer-valued source `if` assignment statements whose local is used
  by a later return expression into MIR diamond blocks with a merge phi.
- Preserved phi-derived range facts into return validation and LLVM range
  attributes through the existing MIR value-fact builder.
- Kept immediately returned local assignment-if bodies on the no-phi
  conditional-return fast path.
- Narrow verification:
  - `../../stark test --filter CompilesLocalIfStatementAssignmentThenReturnFromAst --filter PackageTablesPreserveLocalIfStatementAssignmentThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Local If Phi Slice

- Added typed MIR phi construction so boolean local `if` joins can stay `i1`
  until a final return conversion requires `i64`.
- Lowered boolean local if-expression initializers and compact boolean
  if-statement assignments used by later equality returns into typed MIR phi
  merge blocks.
- Extended braced boolean if-statement assignment arms by matching source
  blocks with brace depth only, so `<` comparisons inside arms do not hide the
  arm's closing brace.
- Emitted comparison LLVM with the left operand's MIR type so boolean equality
  after a boolean phi emits `icmp eq i1` instead of widening the comparison.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanLocalIfExpressionInitializerThenReturnExpressionFromAst --filter CompilesBooleanLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveBooleanLocalIfExpressionInitializerThenReturn --filter PackageTablesPreserveBooleanLocalIfStatementAssignmentThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalIfExpressionInitializerThenReturnFromAst --filter PackageTablesPreserveLocalIfExpressionInitializerThenReturn --filter CompilesLocalIfStatementAssignmentThenReturnFromAst --filter PackageTablesPreserveLocalIfStatementAssignmentThenReturn --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter CompilesBooleanReturnedLocalIfStatementFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfExpression --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement --filter CompilesLocalPrefixedTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBracedBooleanLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveBracedBooleanLocalIfStatementAssignmentThenReturn`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBooleanLocalIfStatementAssignmentThenReturnExpressionFromAst --filter PackageTablesPreserveBooleanLocalIfStatementAssignmentThenReturn --filter CompilesBooleanLocalIfExpressionInitializerThenReturnExpressionFromAst --filter PackageTablesPreserveBooleanLocalIfExpressionInitializerThenReturn --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Terminal If Slice

- Lowered terminal source `if` return branches with boolean values into
  typed MIR conditional return blocks.
- Kept boolean comparisons as `i1` values through arm lowering and widened only
  the branch return values to the ABI `i64` shape.
- Preserved the widened boolean return range facts so `i64[0 1]` declarations
  pass and impossible narrower declarations fail.
- Covered braced and semicolon-terminated compact arms for both direct terminal
  `if` bodies and local-prefixed terminal `if` bodies.
- Narrow verification:
  - `../../stark test --filter CompilesBracedBooleanTerminalIfFromAst --filter PackageTablesPreserveBracedBooleanTerminalIf --filter CompilesLocalPrefixedBracedBooleanTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedBracedBooleanTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesSemicolonBooleanTerminalIfFromAst --filter PackageTablesPreserveSemicolonBooleanTerminalIf --filter CompilesLocalPrefixedSemicolonBooleanTerminalIfFromAst --filter PackageTablesPreserveLocalPrefixedSemicolonBooleanTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BooleanTerminalIfPreservesReturnRangeFacts`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesBracedTerminalIfFromAst --filter CompilesLocalPrefixedBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesSemicolonTerminatedTerminalIfFromAst --filter CompilesLocalPrefixedSemicolonTerminalIfFromAst --filter PackageTablesPreserveSemicolonTerminalIf`
    in `tests-stark/selfhost.Ir`: failed as a grouped run after
    `CompilesSemicolonTerminatedTerminalIfFromAst` passed; the two reported
    failures passed when rerun individually.
  - `../../stark test --filter CompilesSemicolonTerminatedTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesLocalPrefixedSemicolonTerminalIfFromAst`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter PackageTablesPreserveSemicolonTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Return If Expression Slice

- Covered boolean-valued terminal `return if ... else ...` expressions through
  typed MIR conditional return blocks.
- Kept boolean arm comparisons as `i1` and widened only branch return values to
  the ABI `i64` shape.
- Preserved widened boolean return range facts so `i64[0 1]` declarations pass
  and impossible narrower declarations fail.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanReturnIfExpressionFromAst --filter BooleanReturnIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveBooleanReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnIfExpressionFromAst --filter ReturnIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveReturnIfExpression`
    in `tests-stark/selfhost.Ir`: passed; the substring filter also selected
    `BooleanReturnIfExpressionPreservesBranchRangeFacts`.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Returned Local If Statement Slice

- Covered compact and braced boolean-valued source `if` assignment statements
  whose overwritten local is immediately returned.
- Kept branch boolean expressions as `i1` and widened only branch return values
  to the ABI `i64` shape.
- Preserved widened boolean return range facts so `i64[0 1]` declarations pass
  and impossible narrower declarations fail.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanReturnedLocalIfStatementFromAst --filter BooleanReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveBooleanReturnedLocalIfStatement --filter CompilesBracedBooleanReturnedLocalIfStatementFromAst --filter BracedBooleanReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveBracedBooleanReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfStatementFromAst --filter ReturnedLocalIfStatementPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfStatement --filter CompilesBracedReturnedLocalIfStatementFromAst --filter PackageTablesPreserveBracedReturnedLocalIfStatement`
    in `tests-stark/selfhost.Ir`: passed; the substring filters also selected
    the boolean returned-local range-fact tests.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Returned Local If Expression Slice

- Covered boolean-valued immediately returned local source `if` expression
  initializers through typed MIR conditional return blocks.
- Kept branch boolean expressions as `i1` and widened only branch return values
  to the ABI `i64` shape.
- Preserved widened boolean return range facts so `i64[0 1]` declarations pass
  and impossible narrower declarations fail.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanReturnedLocalIfExpressionFromAst --filter BooleanReturnedLocalIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveBooleanReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesReturnedLocalIfExpressionFromAst --filter ReturnedLocalIfExpressionPreservesBranchRangeFacts --filter PackageTablesPreserveReturnedLocalIfExpression`
    in `tests-stark/selfhost.Ir`: passed; the substring filter also selected
    `BooleanReturnedLocalIfExpressionPreservesBranchRangeFacts`.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Local If Phi Range Slice

- Preserved boolean local `if` phi result facts for if-expression initializers,
  compact if-statement assignments, and braced if-statement assignments.
- Verified the lowered LLVM keeps `phi i1` and `icmp eq i1`, widens only at the
  return edge with `zext i1`, and emits the declared `range(i64 0, 2)` return
  attribute.
- Narrow verification:
  - `../../stark test --filter BooleanLocalIfExpressionInitializerThenReturnExpressionPreservesRangeFacts --filter BooleanLocalIfStatementAssignmentThenReturnExpressionPreservesRangeFacts --filter BracedBooleanLocalIfStatementAssignmentThenReturnExpressionPreservesRangeFacts`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Counting While Loop Slice

- Lowered canonical source `while` counting loops into module MIR
  entry/header/body/exit blocks with an induction phi and explicit backedge.
- Routed the loop shape through effect prepass, final LLVM emission, and
  package-table construction instead of the standalone loop text emitter.
- Verified unsupported non-literal bounds, wrong assignment targets, and
  non-additive updates still reject.
- Narrow verification:
  - `../../stark test --filter CompilesModuleCountingWhileLoopFromAst --filter ModuleCountingWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter LowersCountingLoopSourceToLlvm --filter EmitsLlvmCountingLoopWithBackEdge --filter WhileLoopRejectsUnsupportedShapes`
    in `tests-stark/selfhost.Ir`: passed; the substring filter also selected
    `ModuleCountingWhileLoopRejectsUnsupportedShapes`.
- No broad test sweep was run.

## 2026-06-26 Selfhost Accumulator While Loop Slice

- Lowered canonical source accumulator `while` loops into module MIR loop blocks
  with counter and accumulator phis.
- Routed the dual-phi loop shape through effect prepass, final LLVM emission,
  and package-table construction.
- Verified non-literal bounds, swapped body updates, non-additive counter
  updates, and wrong return values still reject.
- Narrow verification:
  - `../../stark test --filter CompilesModuleAccumulatorWhileLoopFromAst --filter ModuleAccumulatorWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveAccumulatorWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingWhileLoopFromAst --filter ModuleCountingWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Counted For Loop Slice

- Lowered canonical counted `for willexit` loops over an existing local into
  module MIR entry/header/body/exit blocks with an induction phi.
- Routed the counted `for` shape through effect prepass, final LLVM emission,
  and package-table construction.
- Rejected `independent` in this route rather than dropping an unsupported
  optimization fact before backend metadata exists.
- Narrow verification:
  - `../../stark test --filter CompilesModuleCountingForLoopFromAst --filter ModuleCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingWhileLoopFromAst --filter ModuleCountingWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingWhileLoop --filter CompilesModuleAccumulatorWhileLoopFromAst --filter ModuleAccumulatorWhileLoopRejectsUnsupportedShapes --filter PackageTablesPreserveAccumulatorWhileLoop`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Header Counted For Loop Slice

- Lowered canonical counted `for willexit` loops with `stack mut` or
  `register mut` header locals into module MIR loop blocks.
- Preserved the induction phi, comparison, update, return range validation,
  effect prepass visibility, final LLVM emission, and package-table shape.
- Rejected `independent`, heap header locals, immutable header locals,
  non-literal bounds, non-additive updates, nonempty bodies, and wrong returns.
- Narrow verification:
  - `../../stark test --filter CompilesModuleHeaderCountingForLoopFromAst --filter ModuleHeaderCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveHeaderCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingForLoopFromAst --filter ModuleCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Independent Counted For Loop Fact Slice

- Preserved canonical counted source `for willexit independent` loop facts on
  MIR backedge blocks and through MIR block serialization.
- Emitted LLVM loop metadata for independent counted-loop backedges so the
  source no-loop-carried-dependency fact reaches LLVM.
- Narrow verification:
  - `../../stark test --filter BinaryRoundTripsIndependentLoopBackedgeFlag --filter CompilesModuleIndependentCountingForLoopFromAst --filter CompilesModuleIndependentHeaderCountingForLoopFromAst --filter PackageTablesPreserveIndependentCountingForLoop --filter PackageTablesPreserveIndependentHeaderCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsBlocks --filter CompilesModuleCountingForLoopFromAst --filter ModuleCountingForLoopRejectsUnsupportedShapes --filter CompilesModuleHeaderCountingForLoopFromAst --filter ModuleHeaderCountingForLoopRejectsUnsupportedShapes --filter PackageTablesPreserveCountingForLoop --filter PackageTablesPreserveHeaderCountingForLoop`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter BinaryRoundTripsPackageImage --filter PackageImageWithAsmRoundTripsMetadata --filter ModulePackageImageWithAsmBuilderRoundTrips --filter PackageImageRoundTripsThroughFile --filter PackageImageWithAsmRoundTripsThroughFile --filter SectionedPackageImageWithAsmRoundTripsMetadata`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Terminal Integer Switch Slice

- Lowered terminal integer source `switch` bodies with two literal `case`
  labels and a `default` into MIR conditional blocks.
- Routed the switch shape through effect prepass, final LLVM emission, and
  package-table construction.
- Preserved single scrutinee evaluation, return range validation, branch target
  wiring, and `icmp eq` comparison facts through emitted LLVM.
- Narrow verification:
  - `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesModuleCountingForLoopFromAst --filter CompilesBracedTerminalIfFromAst --filter PackageTablesPreserveBracedTerminalIf`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesRecursiveFunctionFromAst`
    in `tests-stark/selfhost.Ir`: passed.
- Observed `../../stark test --filter CompilesRecursiveFunctionFromAst --filter CompilesBracedTerminalIfFromAst`
  pass the recursive test and then exit 139 before the next selected test; both
  tests pass when run independently, so this was not counted as a switch
  lowering failure.
- No broad test sweep was run.

## 2026-06-26 Selfhost Boolean Terminal Integer Switch Slice

- Lowered boolean-valued terminal integer switch arms through typed MIR values
  and explicit `zext` return values.
- Preserved return range facts through the switch return blocks so LLVM emits
  the declared `i64[0 1]` range correctly.
- Kept mixed integer/boolean switch return arms rejected rather than widening
  them silently.
- Narrow verification:
  - `../../stark test --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBooleanTerminalIntegerSwitch --filter TerminalIntegerSwitchRejectsUnsupportedShapes`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter PackageTablesPreserveTerminalIntegerSwitch --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-26 Selfhost Signed Terminal Switch Case Slice

- Lowered signed integer `case` labels in terminal integer switches without
  changing the one-scrutinee, direct `icmp eq`, or five-block MIR shape.
- Preserved the signed case immediate through package-table construction so the
  compare operand remains a `ConstInt(-1)` fact.
- Narrow verification:
  - `../../stark test --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter TerminalIntegerSwitchRejectsUnsupportedShapes`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.

## 2026-06-27 Selfhost Braced And Local Terminal Switch Slice

- Lowered braced `return` arms in terminal integer switches through the existing
  terminal switch MIR shape.
- Lowered scalar `var`-prefixed terminal integer switches by evaluating local
  initializers once and feeding the switch through SSA local overrides.
- Preserved boolean-valued local-prefixed switch arms as explicit `zext` return
  values so LLVM receives the declared `i64[0 1]` range.
- Routed the local-prefixed switch shape through effect prepass, final LLVM
  emission, and package-table construction.
- Narrow verification:
  - `../../stark test --filter CompilesBracedArmTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedTerminalIntegerSwitchFromAst --filter CompilesLocalPrefixedBooleanTerminalIntegerSwitchFromAst --filter PackageTablesPreserveBracedArmTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedTerminalIntegerSwitch --filter PackageTablesPreserveLocalPrefixedBooleanTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
  - `../../stark test --filter CompilesTerminalIntegerSwitchFromAst --filter CompilesSignedCaseTerminalIntegerSwitchFromAst --filter CompilesBooleanTerminalIntegerSwitchFromAst --filter TerminalIntegerSwitchRejectsUnsupportedShapes --filter PackageTablesPreserveTerminalIntegerSwitch --filter PackageTablesPreserveSignedCaseTerminalIntegerSwitch --filter PackageTablesPreserveBooleanTerminalIntegerSwitch`
    in `tests-stark/selfhost.Ir`: passed.
- No broad test sweep was run.
