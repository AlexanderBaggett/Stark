# `System.Testing`

`System.Testing` provides the first Stark-native assertion helpers for test
projects run by `stark test`.

It is intentionally imported explicitly:

```stark
import System.Testing
module DemoTests
```

The module is packaged with `System`, but it is not re-exported by the `System`
root so normal programs do not pick up test helper names by default.

## Surface

```stark
public enum TestStatus
{
    Passed, Failed
}

public struct SnapshotDifference
{
    u64[0 2 ** 63 - 1] ExpectedLength;
    u64[0 2 ** 63 - 1] ActualLength;
    u64[0 2 ** 63 - 1] FirstDifference;
    u64[1 2 ** 63 - 1] Line;
    u64[1 2 ** 63 - 1] Column;
}

public enum SnapshotResult
{
    Matched,
    Updated,
    Missing,
    Different(SnapshotDifference),
    Err(System.IO.IOError),
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public struct DiagnosticLocation
{
    bool HasValue;
    ascii FilePath;
    u64[0 2 ** 63 - 1] Line;
    u64[0 2 ** 63 - 1] Column;
    bool HasEndValue;
    u64[0 2 ** 63 - 1] EndLine;
    u64[0 2 ** 63 - 1] EndColumn;
}

public struct Diagnostic
{
    ascii Code;
    DiagnosticSeverity Severity;
    ascii Message;
    ascii Stage;
    DiagnosticLocation Location;
}

public struct TempDirectory
{
    finite ascii View(mut borrow TempDirectory self);
    finite law bool IsActive(borrow TempDirectory self);
    fn System.IO.IOResult<System.Text.OwnedAscii> PathFor(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
    fn System.IO.IOStatus CreateDirectory(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
    fn System.IO.IOStatus DeleteDirectory(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
    fn System.IO.IOStatus WriteText(mut borrow TempDirectory self, ascii relativePath, ascii text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
    fn System.IO.IOStatus WriteText(mut borrow TempDirectory self, ascii relativePath, unicode text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
    fn System.IO.IOStatus WriteTextAtomic(mut borrow TempDirectory self, ascii relativePath, ascii text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
    fn System.IO.IOStatus WriteTextAtomic(mut borrow TempDirectory self, ascii relativePath, unicode text) where overlap(self, relativePath), overlap(self, text), overlap(relativePath, text);
    fn System.IO.IOResult<System.Text.OwnedAscii> ReadText(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
    fn System.IO.IOStatus DeleteFile(mut borrow TempDirectory self, ascii relativePath) where overlap(self, relativePath);
    fn System.IO.IOStatus Cleanup(mut borrow TempDirectory self);
}

public fn System.IO.IOResult<TempDirectory> CreateTempDirectory(ascii prefix);

public finite law bool True(bool condition);
public finite law bool False(bool condition);
public fn bool Fail(ascii message);

public finite law bool Equal(bool expected, bool actual);
public finite law bool Equal(i32[min max] expected, i32[min max] actual);
public finite law bool Equal(i64[min max] expected, i64[min max] actual);
public finite law bool Equal(u32[0 max] expected, u32[0 max] actual);
public finite law bool Equal(u64[0 max] expected, u64[0 max] actual);
public finite law bool Equal(ascii expected, ascii actual);
public finite law bool Equal(unicode expected, unicode actual);

public finite law bool NotEqual(bool expected, bool actual);
public finite law bool NotEqual(i32[min max] expected, i32[min max] actual);
public finite law bool NotEqual(i64[min max] expected, i64[min max] actual);
public finite law bool NotEqual(u32[0 max] expected, u32[0 max] actual);
public finite law bool NotEqual(u64[0 max] expected, u64[0 max] actual);
public finite law bool NotEqual(ascii expected, ascii actual);
public finite law bool NotEqual(unicode expected, unicode actual);

public finite law bool InRange(i32[min max] min, i32[min max] max, i32[min max] actual);
public finite law bool InRange(i64[min max] min, i64[min max] max, i64[min max] actual);
public finite law bool InRange(u32[0 max] min, u32[0 max] max, u32[0 max] actual);
public finite law bool InRange(u64[0 max] min, u64[0 max] max, u64[0 max] actual);
public finite law bool NotInRange(i32[min max] min, i32[min max] max, i32[min max] actual);
public finite law bool NotInRange(i64[min max] min, i64[min max] max, i64[min max] actual);
public finite law bool NotInRange(u32[0 max] min, u32[0 max] max, u32[0 max] actual);
public finite law bool NotInRange(u64[0 max] min, u64[0 max] max, u64[0 max] actual);

public finite law bool Empty(ascii value);
public finite law bool Empty(unicode value);
public finite law bool Empty<T>(borrow T[] values);
public finite law bool Empty<T>(borrow System.Collections.List<T> values);
public finite law bool NotEmpty(ascii value);
public finite law bool NotEmpty(unicode value);
public finite law bool NotEmpty<T>(borrow T[] values);
public finite law bool NotEmpty<T>(borrow System.Collections.List<T> values);
public finite law bool Single<T>(borrow T[] values);
public finite law bool Single<T>(borrow System.Collections.List<T> values);
public finite law bool Count<T>(u64[0 2 ** 63 - 1] expected, borrow T[] values);
public finite law bool Count<T>(u64[0 2 ** 63 - 1] expected, borrow System.Collections.List<T> values);

public finite law bool Contains(ascii value, ascii expected) where overlap(value, expected);
public finite law bool Contains(unicode value, unicode expected) where overlap(value, expected);
public finite law bool DoesNotContain(ascii value, ascii expected) where overlap(value, expected);
public finite law bool DoesNotContain(unicode value, unicode expected) where overlap(value, expected);
public finite law bool StartsWith(ascii value, ascii expected) where overlap(value, expected);
public finite law bool StartsWith(unicode value, unicode expected) where overlap(value, expected);
public finite law bool EndsWith(ascii value, ascii expected) where overlap(value, expected);
public finite law bool EndsWith(unicode value, unicode expected) where overlap(value, expected);
public finite law u64[0 2 ** 63 - 1] CountOccurrences(ascii value, ascii needle) where overlap(value, needle);
public finite law u64[0 2 ** 63 - 1] CountOccurrences(unicode value, unicode needle) where overlap(value, needle);
public finite law bool Occurrences(u64[0 2 ** 63 - 1] expected, ascii value, ascii needle) where overlap(value, needle);
public finite law bool Occurrences(u64[0 2 ** 63 - 1] expected, unicode value, unicode needle) where overlap(value, needle);

public inline finite law bool DiagnosticCode(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticSeverityIs(DiagnosticSeverity expected, borrow Diagnostic diagnostic);
public inline finite law bool DiagnosticStage(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticMessageEqual(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticMessageContains(borrow Diagnostic diagnostic, ascii expected) where overlap(diagnostic, expected);
public inline finite law bool DiagnosticHasLocation(borrow Diagnostic diagnostic);
public inline finite law bool DiagnosticHasEndLocation(borrow Diagnostic diagnostic);
public inline finite law bool DiagnosticFilePath(ascii expected, borrow Diagnostic diagnostic) where overlap(expected, diagnostic);
public inline finite law bool DiagnosticAt(borrow Diagnostic diagnostic, u64[0 2 ** 63 - 1] line, u64[0 2 ** 63 - 1] column);
public inline finite law bool DiagnosticEndsAt(borrow Diagnostic diagnostic, u64[0 2 ** 63 - 1] endLine, u64[0 2 ** 63 - 1] endColumn);
public inline finite law bool DiagnosticMatches(borrow Diagnostic diagnostic, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains) where overlap(diagnostic, code), overlap(diagnostic, stage), overlap(diagnostic, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public inline finite law bool DiagnosticMatchesAt(borrow Diagnostic diagnostic, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains, u64[0 2 ** 63 - 1] line, u64[0 2 ** 63 - 1] column) where overlap(diagnostic, code), overlap(diagnostic, stage), overlap(diagnostic, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public finite law bool DiagnosticsCount(u64[0 2 ** 63 - 1] expected, borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsEmpty(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsNotEmpty(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsContainCode(borrow Diagnostic[] diagnostics, ascii code) where overlap(diagnostics, code);
public finite law bool DiagnosticsContainMessage(borrow Diagnostic[] diagnostics, ascii messageContains) where overlap(diagnostics, messageContains);
public finite law bool DiagnosticsContain(borrow Diagnostic[] diagnostics, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains) where overlap(diagnostics, code), overlap(diagnostics, stage), overlap(diagnostics, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public finite law bool DiagnosticsContainAt(borrow Diagnostic[] diagnostics, ascii code, DiagnosticSeverity severity, ascii stage, ascii messageContains, u64[0 2 ** 63 - 1] line, u64[0 2 ** 63 - 1] column) where overlap(diagnostics, code), overlap(diagnostics, stage), overlap(diagnostics, messageContains), overlap(code, stage), overlap(code, messageContains), overlap(stage, messageContains);
public finite law u64[0 2 ** 63 - 1] DiagnosticsSeverityCount(DiagnosticSeverity severity, borrow Diagnostic[] diagnostics);
public finite law u64[0 2 ** 63 - 1] DiagnosticsErrorCount(borrow Diagnostic[] diagnostics);
public finite law u64[0 2 ** 63 - 1] DiagnosticsWarningCount(borrow Diagnostic[] diagnostics);
public finite law u64[0 2 ** 63 - 1] DiagnosticsInfoCount(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsHaveNoErrors(borrow Diagnostic[] diagnostics);
public finite law bool DiagnosticsHaveErrors(borrow Diagnostic[] diagnostics);

public finite law bool TypeIs<TActual, TExpected>();
public finite law bool TypeIsBool<T>();
public finite law bool TypeIsInteger<T>();
public finite law bool TypeIsFloat<T>();
public finite law bool TypeIsRawPointer<T>();
public finite law bool TypeIsFixedArray<T>();
public finite law bool TypeIsSlice<T>();
public finite law bool TypeIsDynamic<T>();
public finite law bool TypeIsFunctionPointer<T>();
public finite law bool TypeIsClosure<T>();
public finite law bool TypeIsNamed<T>();
public finite law bool TypeIsStruct<T>();
public finite law bool TypeIsRecord<T>();
public finite law bool TypeIsEnum<T>();
public finite law bool TypeIsTrait<T>();
public finite law bool TypeIsDoctrine<T>();
public finite law bool TypeIsDynTrait<T>();
public finite law bool TypeHasConcreteLayout<T>();
public finite law bool TypeIsZeroSized<T>();
public finite law bool TypeSizeIs<T>(u64[0 max] expected);
public finite law bool TypeAlignIs<T>(u64[0 max] expected);
public finite law bool TypeDisplayName<T>(ascii expected);
public finite law bool TypeBaseName<T>(ascii expected);
public finite law bool TypeModuleName<T>(ascii expected);
public finite law bool TypeIsGenericInstantiation<T>();
public finite law bool TypeArgumentCount<T>(u64[0 max] expected);
public finite law bool TypeComptimeArgumentCount<T>(u64[0 max] expected);

public finite law bool OptionSome<T>(borrow System.Core.Option<T> value);
public finite law bool OptionNone<T>(borrow System.Core.Option<T> value);
public finite law bool ResultOk<T, E>(borrow System.Core.Result<T, E> value);
public finite law bool ResultErr<T, E>(borrow System.Core.Result<T, E> value);

public finite law bool ProcessExitCode(i32[min max] expected, borrow System.Process.ProcessOutput output);
public finite law bool ProcessTimedOut(borrow System.Process.ProcessOutput output);
public finite law bool ProcessCompleted(borrow System.Process.ProcessOutput output);
public finite law bool ProcessStdoutEqual(ascii expected, borrow System.Process.ProcessOutput output) where overlap(expected, output);
public finite law bool ProcessStderrEqual(ascii expected, borrow System.Process.ProcessOutput output) where overlap(expected, output);
public finite law bool ProcessStdoutContains(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStderrContains(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law u64[0 2 ** 63 - 1] ProcessStdoutCountOccurrences(borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law u64[0 2 ** 63 - 1] ProcessStderrCountOccurrences(borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law bool ProcessStdoutOccurrences(u64[0 2 ** 63 - 1] expected, borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law bool ProcessStderrOccurrences(u64[0 2 ** 63 - 1] expected, borrow System.Process.ProcessOutput output, ascii needle) where overlap(output, needle);
public finite law bool ProcessStdoutStartsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStderrStartsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStdoutEndsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStderrEndsWith(borrow System.Process.ProcessOutput output, ascii expected) where overlap(output, expected);
public finite law bool ProcessStdoutEmpty(borrow System.Process.ProcessOutput output);
public finite law bool ProcessStderrEmpty(borrow System.Process.ProcessOutput output);
public finite law bool ProcessOutputEqual(i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr, borrow System.Process.ProcessOutput output) where overlap(expectedStdout, expectedStderr), overlap(expectedStdout, output), overlap(expectedStderr, output);
public unsafe fn bool RunProcessMatches(mut borrow System.Process.ProcessCommand command, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(command, expectedStdout), overlap(command, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessMatchesWithInput(mut borrow System.Process.ProcessCommand command, ascii input, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(command, input), overlap(command, expectedStdout), overlap(command, expectedStderr), overlap(input, expectedStdout), overlap(input, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessTimesOut(mut borrow System.Process.ProcessCommand command, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn bool RunProcessTimesOutWithInput(mut borrow System.Process.ProcessCommand command, ascii input, u32[0 2 ** 31 - 1] timeoutMilliseconds) where overlap(command, input);
public unsafe fn bool RunProcessMatches(ascii executable, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(executable, expectedStdout), overlap(executable, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessMatchesWithInput(ascii executable, ascii input, i32[min max] expectedExitCode, ascii expectedStdout, ascii expectedStderr) where overlap(executable, input), overlap(executable, expectedStdout), overlap(executable, expectedStderr), overlap(input, expectedStdout), overlap(input, expectedStderr), overlap(expectedStdout, expectedStderr);
public unsafe fn bool RunProcessTimesOut(ascii executable, u32[0 2 ** 31 - 1] timeoutMilliseconds);
public unsafe fn bool RunProcessTimesOutWithInput(ascii executable, ascii input, u32[0 2 ** 31 - 1] timeoutMilliseconds) where overlap(executable, input);

public finite law TestStatus Status(bool assertion);
public fn u8[0 1] RunFact(ascii name, bool assertion);
public fn u8[0 1] RunFact(unicode name, bool assertion);
public fn u8[0 1] SkipFact(ascii name, ascii reason);
public fn u8[0 1] SkipFact(unicode name, unicode reason);
public finite law i32[min max] ExitCode(u32[0 2 ** 31 - 1] failureCount);
public fn void Exit(u32[0 2 ** 31 - 1] failureCount);

public finite law SnapshotResult CompareSnapshotText(borrow System.Text.OwnedAscii expected, ascii actual) where overlap(expected, actual);
public fn SnapshotResult VerifySnapshot(ascii path, ascii actual);
public fn SnapshotResult UpdateSnapshot(ascii path, ascii actual);
public fn SnapshotResult VerifyOrUpdateSnapshot(ascii path, ascii actual, bool update);
public finite law bool SnapshotSucceeded(SnapshotResult result);
public fn System.Memory.MemoryStatus AppendSnapshotDifference(mut borrow System.Text.OwnedAscii writer, borrow SnapshotDifference difference);
```

## Behavior

- Pure assertions are `finite law` functions returning ordinary `bool` values;
  they do not throw or unwind. Effectful helpers such as `Fail`, process
  runners, fixture IO, snapshot writes, and runner output remain plain `fn`.
- Text assertions use allocation-free `System.Text` scans, including
  non-overlapping occurrence counts. Empty needles count as `0`.
- Root `System.Option<T>` and `System.Result<T, E>` shape predicates borrow the
  enum and test only the tag; payload-specific checks should use an explicit
  caller switch.
- Structured diagnostic assertions work over caller-owned `Diagnostic` values
  that mirror the host compiler test protocol fields: code, severity, message,
  stage, and optional source/end location. Slice-level diagnostic scans use
  indexed finite-law loops so they do not allocate and have predictable bounds.
- Type assertions are compile-time structural fact wrappers exposed as ordinary
  finite-law bool predicates. Layout-sensitive predicates return `false` when
  the type has no concrete layout.
- Process output assertions compare `System.Process.ProcessOutput` stdout and
  stderr byte slices directly against ASCII text without allocation, including
  non-overlapping occurrence counts.
  `RunProcessMatches` and `RunProcessMatchesWithInput` run a command and compare
  exit code, stdout, and stderr in one bool-returning test helper. Timeout
  predicates and `RunProcessTimesOut*` helpers use `ProcessOutput.WasTimedOut()`
  so timed-out processes remain ordinary assertion values.
- Collection-shape assertions support slices and `System.Collections.List<T>`.
- `CreateTempDirectory` returns an owned `TempDirectory` fixture rooted in the
  platform temp directory. Fixture paths are relative-only; empty paths, rooted
  paths, and parent traversal are rejected with `IOError.InvalidPath`.
- `TempDirectory` file helpers use `System.IO.File` and `System.FileSystem`
  underneath, including atomic text replacement and recursive cleanup.
- `Cleanup` deletes the fixture tree and deactivates the fixture. The fixture
  also has best-effort drop cleanup, but tests should call `Cleanup` explicitly
  when they need to observe errors.
- Snapshot helpers compare ASCII/UTF-8 text without runtime reflection or hidden
  update policy. `VerifySnapshot` checks an existing file, `UpdateSnapshot`
  atomically writes a snapshot, and `VerifyOrUpdateSnapshot` writes only when
  the caller passes `update = true`.
- Snapshot comparison normalizes CRLF and LF line endings while reporting the
  first logical difference as line/column facts in `SnapshotDifference`.
- `AppendSnapshotDifference` writes a stable short failure description into
  caller-owned text storage.
- `RunFact` writes a concise pass/fail line and returns `1` for a failed fact.
  `SkipFact` writes a concise skipped line and returns `0`.
- `stark test` generates an explicit `main` for test roots that contain
  `[Fact]` or `[Theory]` metadata. The generated runner enumerates tests at build time,
  applies any `--filter` selections, applies `[Platform(...)]` and
  `[SkipPlatform(...)]` gates from the selected target triple, applies
  `[Collection(...)]` / `[Serial]` scheduling groups, calls `RunFact` for
  runnable facts, calls `SkipFact` for gated-out facts, and returns
  `System.Testing.ExitCode(failureCount)`.
- `[Fact]` functions must be non-generic, no-argument `bool` functions with a
  body. Supported facts are top-level functions or `static` methods on structs
  and records.
- `[Theory]` functions follow the same static/non-generic/body/`bool` return
  rules, but may take parameters. Each `[InlineData(...)]` row expands to one
  generated direct call, and row arity must match the function parameter count.
  Inline data accepts string literals, booleans, signed integer literals, and
  qualified names. Filters match the generated row display name, for example
  `Adds(1, 2, 3)`.
- Larger or computed theory tables use `[MemberData(provider, rowType, count)]`.
  `provider` is a function called once per selected row with the zero-based row
  index and returning `rowType`; `count` is a positive integer literal. The
  generated runner materializes one stack row local per selected row and calls
  the theory with row fields matching the theory parameter names. Optional field
  names after `count` map row fields by parameter order, for example
  `[MemberData(AddRows, AddRow, 2, Left, Right, Expected)]`. Filters match names
  such as `Adds[AddRows:1]`.
- Platform gates accept OS selectors such as `linux`, `windows`, `macos`, target
  architecture selectors such as `x64` or `arm64`, `os.arch` pairs such as
  `linux.x64`, or exact target triple strings. Type-level gates on structs and
  records combine with member-level gates; `[SkipPlatform]` wins when it matches.
- `[Collection(name)]` names a serial scheduling collection. The name may be a
  string literal or qualified identifier and must be non-empty, trimmed, and free
  of control characters. `[Serial]` is shorthand for `[Collection("Serial")]`.
  Type-level collections on structs and records apply to contained fact methods;
  member-level collections may repeat the same name but cannot override it with a
  different name. The current generated runner is single-threaded and emits facts
  in stable collection groups at the first occurrence of each named collection,
  preserving fact order inside the collection and adding no runtime cost for
  uncollected facts.
- Test roots that contain `[Fact]` or `[Theory]` metadata should not declare their own
  `main`; manual runners remain a compatibility path for bootstrap test
  executables with no generated test metadata.
- Rich diagnostic rendering and JSON-to-`Diagnostic` adapters remain tracked by
  the self-host test-infrastructure work. The low-level structured predicates
  are first-class `System.Testing` helpers now.

## Example

```stark
import System.Testing
module DemoTests

[Fact]
fn bool AddsNumbers()
{
    return System.Testing.Equal(4, 2 + 2);
}

record AddRow(i32[min max] Left, i32[min max] Right, i32[min max] Expected) { }

finite law AddRow AddRows(u64[0 2 ** 63 - 1] index)
{
    switch (index)
    {
        case 0:
            return new AddRow(2, 2, 4);
        default:
            return new AddRow(-3, 5, 2);
    }
}

[Theory]
[MemberData(AddRows, AddRow, 2, Left, Right, Expected)]
finite law bool AddsExamples(i32[min max] left, i32[min max] right, i32[min max] expected)
{
    return left + right == expected;
}
```

Run a selected subset by matching the generated fact display name:

```bash
stark test --filter AddsNumbers
stark test --filter AddRows:1
```
