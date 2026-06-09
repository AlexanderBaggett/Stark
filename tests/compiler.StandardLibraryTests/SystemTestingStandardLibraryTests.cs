using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemTestingStandardLibraryTests : StandardLibraryTestSuite
{
    private const string TestingAssertionsProgram =
        """
        import System
        import System.Testing
        import System.Text
        module DemoTests

        fn bool IsOk(System.IO.IOStatus status)
        {
            switch (status)
            {
                case System.IO.IOStatus.Ok:
                    return true;
                case System.IO.IOStatus.Err(var error):
                    return false;
            }
        }

        fn bool IsInvalidPath(System.IO.IOStatus status)
        {
            switch (status)
            {
                case System.IO.IOStatus.Ok:
                    return false;
                case System.IO.IOStatus.Err(var error):
                    switch (error)
                    {
                        case System.IO.IOError.InvalidPath:
                            return true;
                        case System.IO.IOError.NotFound:
                            return false;
                        case System.IO.IOError.PermissionDenied:
                            return false;
                        case System.IO.IOError.AlreadyExists:
                            return false;
                        case System.IO.IOError.BrokenPipe:
                            return false;
                        case System.IO.IOError.DiskFull:
                            return false;
                        case System.IO.IOError.Unknown(var code):
                            return false;
                    }
            }
        }

        fn bool ReadEquals(System.IO.IOResult<System.Text.OwnedAscii> result, ascii expected)
        {
            switch (result)
            {
                case System.IO.IOResult<System.Text.OwnedAscii>.Err(var error):
                    return false;
                case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var text):
                    return System.Text.OwnedAsciiEqualsAscii(text, expected);
            }
        }

        fn bool PathMissing(System.IO.IOResult<bool> result)
        {
            switch (result)
            {
                case System.IO.IOResult<bool>.Err(var error):
                    return false;
                case System.IO.IOResult<bool>.Ok(var exists):
                    return !exists;
            }
        }

        fn bool SnapshotMatched(System.Testing.SnapshotResult result)
        {
            switch (result)
            {
                case System.Testing.SnapshotResult.Matched:
                    return true;
                case System.Testing.SnapshotResult.Updated:
                    return false;
                case System.Testing.SnapshotResult.Missing:
                    return false;
                case System.Testing.SnapshotResult.Different(var difference):
                    return false;
                case System.Testing.SnapshotResult.Err(var error):
                    return false;
            }
        }

        fn bool SnapshotUpdated(System.Testing.SnapshotResult result)
        {
            switch (result)
            {
                case System.Testing.SnapshotResult.Matched:
                    return false;
                case System.Testing.SnapshotResult.Updated:
                    return true;
                case System.Testing.SnapshotResult.Missing:
                    return false;
                case System.Testing.SnapshotResult.Different(var difference):
                    return false;
                case System.Testing.SnapshotResult.Err(var error):
                    return false;
            }
        }

        fn bool SnapshotMissing(System.Testing.SnapshotResult result)
        {
            switch (result)
            {
                case System.Testing.SnapshotResult.Matched:
                    return false;
                case System.Testing.SnapshotResult.Updated:
                    return false;
                case System.Testing.SnapshotResult.Missing:
                    return true;
                case System.Testing.SnapshotResult.Different(var difference):
                    return false;
                case System.Testing.SnapshotResult.Err(var error):
                    return false;
            }
        }

        fn bool SnapshotDifferentAt(System.Testing.SnapshotResult result, u64[1 2 ** 63 - 1] line, u64[1 2 ** 63 - 1] column)
        {
            switch (result)
            {
                case System.Testing.SnapshotResult.Matched:
                    return false;
                case System.Testing.SnapshotResult.Updated:
                    return false;
                case System.Testing.SnapshotResult.Missing:
                    return false;
                case System.Testing.SnapshotResult.Different(var difference):
                    return difference.Line == line && difference.Column == column;
                case System.Testing.SnapshotResult.Err(var error):
                    return false;
            }
        }

        [Fact]
        fn bool AdditionWorks()
        {
            return System.Testing.Equal(4, 2 + 2);
        }

        [Fact]
        fn bool RichAssertionsWork()
        {
            stack System.Collections.List<i32[min max]> emptyValues = new();
            stack System.Collections.List<i32[min max]> oneValue = new();
            stack System.Memory.MemoryStatus oneStatus = oneValue.Push(10);
            stack System.Collections.List<i32[min max]> pair = new();
            stack System.Memory.MemoryStatus firstStatus = pair.Push(10);
            stack System.Memory.MemoryStatus secondStatus = pair.Push(20);

            return System.Testing.NotEqual(4, 5)
                && oneStatus == System.Memory.MemoryStatus.Ok
                && firstStatus == System.Memory.MemoryStatus.Ok
                && secondStatus == System.Memory.MemoryStatus.Ok
                && System.Testing.InRange(10, 20, 15)
                && System.Testing.NotInRange(10, 20, 25)
                && System.Testing.Empty("")
                && System.Testing.NotEmpty("compiler")
                && System.Testing.Empty(emptyValues)
                && System.Testing.NotEmpty(pair)
                && System.Testing.Single(oneValue)
                && System.Testing.Count(2, pair)
                && System.Testing.Contains("self-host prep", "host")
                && System.Testing.DoesNotContain("self-host prep", "legacy")
                && System.Testing.StartsWith("self-host prep", "self")
                && System.Testing.EndsWith("self-host prep", "prep")
                && System.Testing.Contains((unicode)"pipeline", (unicode)"line");
        }

        [Fact]
        fn bool TempFixturesWork()
        {
            stack mut System.Testing.TempDirectory fixture = new();
            switch (System.Testing.CreateTempDirectory("stark-test-fixture-"))
            {
                case System.IO.IOResult<System.Testing.TempDirectory>.Err(var error):
                    return false;
                case System.IO.IOResult<System.Testing.TempDirectory>.Ok(var value):
                    fixture = value;
            }

            stack mut System.Text.OwnedAscii rootPath = new();
            if (System.Text.FromAscii(rootPath, fixture.View()) != System.Memory.MemoryStatus.Ok)
            {
                fixture.Cleanup();
                return false;
            }

            if (!fixture.IsActive())
            {
                fixture.Cleanup();
                return false;
            }

            if (!IsInvalidPath(fixture.WriteText("../escape.txt", "no")))
            {
                fixture.Cleanup();
                return false;
            }

            if (!IsOk(fixture.CreateDirectory("empty")))
            {
                fixture.Cleanup();
                return false;
            }

            if (!IsOk(fixture.WriteText("input.txt", "alpha")))
            {
                fixture.Cleanup();
                return false;
            }

            if (!ReadEquals(fixture.ReadText("input.txt"), "alpha"))
            {
                fixture.Cleanup();
                return false;
            }

            if (!IsOk(fixture.WriteTextAtomic("input.txt", "beta")))
            {
                fixture.Cleanup();
                return false;
            }

            if (!ReadEquals(fixture.ReadText("input.txt"), "beta"))
            {
                fixture.Cleanup();
                return false;
            }

            if (!IsOk(fixture.Cleanup()))
            {
                return false;
            }

            return !fixture.IsActive() && PathMissing(System.FileSystem.Exists(rootPath.View()));
        }

        [Fact]
        fn bool SnapshotsWork()
        {
            stack mut System.Testing.TempDirectory fixture = new();
            switch (System.Testing.CreateTempDirectory("stark-test-snapshot-"))
            {
                case System.IO.IOResult<System.Testing.TempDirectory>.Err(var error):
                    return false;
                case System.IO.IOResult<System.Testing.TempDirectory>.Ok(var value):
                    fixture = value;
            }

            stack mut System.Text.OwnedAscii expectedPath = new();
            switch (fixture.PathFor("expected.snap"))
            {
                case System.IO.IOResult<System.Text.OwnedAscii>.Err(var error):
                    fixture.Cleanup();
                    return false;
                case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var value):
                    expectedPath = value;
            }

            stack mut System.Text.OwnedAscii missingPath = new();
            switch (fixture.PathFor("created.snap"))
            {
                case System.IO.IOResult<System.Text.OwnedAscii>.Err(var error):
                    fixture.Cleanup();
                    return false;
                case System.IO.IOResult<System.Text.OwnedAscii>.Ok(var value):
                    missingPath = value;
            }

            if (!IsOk(fixture.WriteText("expected.snap", "first\nsecond\n")))
            {
                fixture.Cleanup();
                return false;
            }

            if (!SnapshotMatched(System.Testing.VerifySnapshot(expectedPath.View(), "first\r\nsecond\n")))
            {
                fixture.Cleanup();
                return false;
            }

            if (!SnapshotDifferentAt(System.Testing.VerifySnapshot(expectedPath.View(), "first\nchanged\n"), 2, 1))
            {
                fixture.Cleanup();
                return false;
            }

            stack mut System.Text.OwnedAscii diffText = new();
            stack System.Testing.SnapshotDifference difference = new System.Testing.SnapshotDifference()
            {
                ExpectedLength = 5,
                ActualLength = 7,
                FirstDifference = 2,
                Line = 3,
                Column = 4
            };
            if (System.Testing.AppendSnapshotDifference(diffText, difference) != System.Memory.MemoryStatus.Ok)
            {
                fixture.Cleanup();
                return false;
            }

            if (!System.Testing.Contains(diffText.View(), "line 3"))
            {
                fixture.Cleanup();
                return false;
            }

            if (!SnapshotMissing(System.Testing.VerifySnapshot(missingPath.View(), "created\n")))
            {
                fixture.Cleanup();
                return false;
            }

            if (!SnapshotUpdated(System.Testing.VerifyOrUpdateSnapshot(missingPath.View(), "created\n", true)))
            {
                fixture.Cleanup();
                return false;
            }

            if (!System.Testing.SnapshotSucceeded(System.Testing.VerifySnapshot(missingPath.View(), "created\r\n")))
            {
                fixture.Cleanup();
                return false;
            }

            return IsOk(fixture.Cleanup());
        }

        export fn i32[min max] main()
        {
            stack mut u8[0 1] failed = 0;
            if (System.Testing.RunFact("AdditionWorks", AdditionWorks()) != 0)
            {
                failed = 1;
            }

            if (System.Testing.RunFact("RichAssertionsWork", RichAssertionsWork()) != 0)
            {
                failed = 1;
            }

            if (System.Testing.RunFact("TempFixturesWork", TempFixturesWork()) != 0)
            {
                failed = 1;
            }

            if (System.Testing.RunFact("SnapshotsWork", SnapshotsWork()) != 0)
            {
                failed = 1;
            }

            return System.Testing.ExitCode(failed);
        }
        """;

    [Fact]
    public void StdLibSourceTestingHelpersCompileWithExplicitFactRunner()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "SystemTestingCompile.stark");

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                TestingAssertionsProgram,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public async Task SourceStdLibTestingRichAssertionsExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-testing-assertions-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, TestingAssertionsProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Contains("ok AdditionWorks", execution.Stdout, StringComparison.Ordinal);
            Assert.Contains("ok RichAssertionsWork", execution.Stdout, StringComparison.Ordinal);
            Assert.Contains("ok TempFixturesWork", execution.Stdout, StringComparison.Ordinal);
            Assert.Contains("ok SnapshotsWork", execution.Stdout, StringComparison.Ordinal);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public void StdLibTestingModuleStaysRawPointerFreeAndExplicit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testingSource = File.ReadAllText(Path.Combine(repositoryRoot, "stdlib", "src", "System", "Testing.stark"));
        var systemSource = File.ReadAllText(Path.Combine(repositoryRoot, "stdlib", "src", "System.stark"));

        Assert.DoesNotContain("rawptr<", testingSource, StringComparison.Ordinal);
        Assert.DoesNotContain("rawmutptr<", testingSource, StringComparison.Ordinal);
        Assert.Contains("import System.Testing", systemSource, StringComparison.Ordinal);
        Assert.DoesNotContain("export import System.Testing", systemSource, StringComparison.Ordinal);
    }
}
