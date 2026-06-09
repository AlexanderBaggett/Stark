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

public fn bool True(bool condition);
public fn bool False(bool condition);
public fn bool Fail(ascii message);

public fn bool Equal(bool expected, bool actual);
public fn bool Equal(i32[min max] expected, i32[min max] actual);
public fn bool Equal(i64[min max] expected, i64[min max] actual);
public fn bool Equal(u32[0 max] expected, u32[0 max] actual);
public fn bool Equal(u64[0 max] expected, u64[0 max] actual);
public fn bool Equal(ascii expected, ascii actual);
public fn bool Equal(unicode expected, unicode actual);

public fn bool NotEqual(bool expected, bool actual);
public fn bool NotEqual(i32[min max] expected, i32[min max] actual);
public fn bool NotEqual(i64[min max] expected, i64[min max] actual);
public fn bool NotEqual(u32[0 max] expected, u32[0 max] actual);
public fn bool NotEqual(u64[0 max] expected, u64[0 max] actual);
public fn bool NotEqual(ascii expected, ascii actual);
public fn bool NotEqual(unicode expected, unicode actual);

public fn bool InRange(i32[min max] min, i32[min max] max, i32[min max] actual);
public fn bool InRange(i64[min max] min, i64[min max] max, i64[min max] actual);
public fn bool InRange(u32[0 max] min, u32[0 max] max, u32[0 max] actual);
public fn bool InRange(u64[0 max] min, u64[0 max] max, u64[0 max] actual);
public fn bool NotInRange(i32[min max] min, i32[min max] max, i32[min max] actual);
public fn bool NotInRange(i64[min max] min, i64[min max] max, i64[min max] actual);
public fn bool NotInRange(u32[0 max] min, u32[0 max] max, u32[0 max] actual);
public fn bool NotInRange(u64[0 max] min, u64[0 max] max, u64[0 max] actual);

public fn bool Empty(ascii value);
public fn bool Empty(unicode value);
public fn bool Empty<T>(borrow T[] values);
public fn bool Empty<T>(borrow System.Collections.List<T> values);
public fn bool NotEmpty(ascii value);
public fn bool NotEmpty(unicode value);
public fn bool NotEmpty<T>(borrow T[] values);
public fn bool NotEmpty<T>(borrow System.Collections.List<T> values);
public fn bool Single<T>(borrow T[] values);
public fn bool Single<T>(borrow System.Collections.List<T> values);
public fn bool Count<T>(u64[0 2 ** 63 - 1] expected, borrow T[] values);
public fn bool Count<T>(u64[0 2 ** 63 - 1] expected, borrow System.Collections.List<T> values);

public fn bool Contains(ascii value, ascii expected) where overlap(value, expected);
public fn bool Contains(unicode value, unicode expected) where overlap(value, expected);
public fn bool DoesNotContain(ascii value, ascii expected) where overlap(value, expected);
public fn bool DoesNotContain(unicode value, unicode expected) where overlap(value, expected);
public fn bool StartsWith(ascii value, ascii expected) where overlap(value, expected);
public fn bool StartsWith(unicode value, unicode expected) where overlap(value, expected);
public fn bool EndsWith(ascii value, ascii expected) where overlap(value, expected);
public fn bool EndsWith(unicode value, unicode expected) where overlap(value, expected);

public fn TestStatus Status(bool assertion);
public fn u8[0 1] RunFact(ascii name, bool assertion);
public fn u8[0 1] RunFact(unicode name, bool assertion);
public fn i32[min max] ExitCode(u32[0 2 ** 31 - 1] failureCount);
public fn void Exit(u32[0 2 ** 31 - 1] failureCount);

public fn SnapshotResult CompareSnapshotText(borrow System.Text.OwnedAscii expected, ascii actual) where overlap(expected, actual);
public fn SnapshotResult VerifySnapshot(ascii path, ascii actual);
public fn SnapshotResult UpdateSnapshot(ascii path, ascii actual);
public fn SnapshotResult VerifyOrUpdateSnapshot(ascii path, ascii actual, bool update);
public finite law bool SnapshotSucceeded(SnapshotResult result);
public fn System.Memory.MemoryStatus AppendSnapshotDifference(mut borrow System.Text.OwnedAscii writer, borrow SnapshotDifference difference);
```

## Behavior

- Assertions return ordinary `bool` values; they do not throw or unwind.
- Text assertions use allocation-free `System.Text` scans.
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
- `stark test` generates an explicit `main` for test roots that contain
  `[Fact]` metadata. The generated runner enumerates facts at build time,
  applies any `--filter` selections, calls `RunFact`, and returns
  `System.Testing.ExitCode(failureCount)`.
- `[Fact]` functions must be non-generic, no-argument `bool` functions with a
  body. Supported facts are top-level functions or `static` methods on structs
  and records.
- Test roots that contain `[Fact]` metadata should not declare their own
  `main`; manual runners remain a compatibility path for no-`[Fact]` bootstrap
  test executables.
- Diagnostic/type assertions and root `Option<T>` / `Result<T, E>` predicates
  are still tracked by the self-host test-infrastructure work. They need the
  compiler artifact API and a cleaner root `System`/`System.Testing` visibility
  shape before they can be first-class `System.Testing` helpers.

## Example

```stark
import System.Testing
module DemoTests

[Fact]
fn bool AddsNumbers()
{
    return System.Testing.Equal(4, 2 + 2);
}
```

Run a selected subset by matching the generated fact display name:

```bash
stark test --filter AddsNumbers
```
