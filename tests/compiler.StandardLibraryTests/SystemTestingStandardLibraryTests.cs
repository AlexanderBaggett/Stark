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

        struct AssertionBox
        {
            i32[min max] Value;
        }

        struct EmptyMarker
        {
        }

        enum AssertionToken
        {
            End,
            Number(i32[min max]),
        }

        finite law bool IsOk(System.IO.IOStatus status)
        {
            switch (status)
            {
                case System.IO.IOStatus.Ok:
                    return true;
                case System.IO.IOStatus.Err(var error):
                    return false;
            }
        }

        finite law bool IsInvalidPath(System.IO.IOStatus status)
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

        finite law bool PathMissing(System.IO.IOResult<bool> result)
        {
            switch (result)
            {
                case System.IO.IOResult<bool>.Err(var error):
                    return false;
                case System.IO.IOResult<bool>.Ok(var exists):
                    return !exists;
            }
        }

        finite law bool SnapshotMatched(System.Testing.SnapshotResult result)
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

        finite law bool SnapshotUpdated(System.Testing.SnapshotResult result)
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

        finite law bool SnapshotMissing(System.Testing.SnapshotResult result)
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

        finite law bool SnapshotDifferentAt(System.Testing.SnapshotResult result, u64[1 2 ** 63 - 1] line, u64[1 2 ** 63 - 1] column)
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
        finite law bool AdditionWorks()
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
            stack System.Option<i32[min max]> someValue = System.Option<i32[min max]>.Some(7);
            stack System.Option<i32[min max]> noneValue = System.Option<i32[min max]>.None;
            stack System.Result<i32[min max], i32[min max]> okValue = System.Result<i32[min max], i32[min max]>.Ok(11);
            stack System.Result<i32[min max], i32[min max]> errValue = System.Result<i32[min max], i32[min max]>.Err(13);

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
                && System.Testing.CountOccurrences("abababa", "aba") == 2
                && System.Testing.Occurrences(3, "self-host self-test self-check", "self")
                && System.Testing.Occurrences(0, "compiler", "")
                && System.Testing.Contains((unicode)"pipeline", (unicode)"line")
                && System.Testing.CountOccurrences((unicode)"na-na-na", (unicode)"na") == 3
                && System.Testing.Occurrences(2, (unicode)"stage0 stage1", (unicode)"stage")
                && System.Testing.OptionSome(someValue)
                && !System.Testing.OptionNone(someValue)
                && System.Testing.OptionNone(noneValue)
                && !System.Testing.OptionSome(noneValue)
                && System.Testing.ResultOk(okValue)
                && !System.Testing.ResultErr(okValue)
                && System.Testing.ResultErr(errValue)
                && !System.Testing.ResultOk(errValue);
        }

        finite law bool DiagnosticCountAssertionsWork(borrow System.Testing.Diagnostic[] diagnosticSlice)
        {
            return System.Testing.DiagnosticsCount(2, diagnosticSlice)
                && System.Testing.DiagnosticsNotEmpty(diagnosticSlice)
                && System.Testing.DiagnosticsErrorCount(diagnosticSlice) == 1
                && System.Testing.DiagnosticsWarningCount(diagnosticSlice) == 1
                && System.Testing.DiagnosticsInfoCount(diagnosticSlice) == 0
                && System.Testing.DiagnosticsHaveErrors(diagnosticSlice)
                && !System.Testing.DiagnosticsHaveNoErrors(diagnosticSlice);
        }

        finite law bool DiagnosticContainAssertionsWork(borrow System.Testing.Diagnostic[] diagnosticSlice)
        {
            return System.Testing.DiagnosticsContainCode(diagnosticSlice, "STK3004")
                && System.Testing.DiagnosticsContainMessage(diagnosticSlice, "Unknown type")
                && System.Testing.DiagnosticsContain(
                    diagnosticSlice,
                    "STK3004",
                    System.Testing.DiagnosticSeverity.Error,
                    "type-check",
                    "Missing")
                && System.Testing.DiagnosticsContainAt(
                    diagnosticSlice,
                    "STK3004",
                    System.Testing.DiagnosticSeverity.Error,
                    "type-check",
                    "Unknown type",
                    7,
                    13);
        }

        finite law bool DiagnosticDirectBoolAssertionsWork(borrow System.Testing.Diagnostic[] diagnostics)
        {
            return System.Testing.DiagnosticHasLocation(diagnostics[0])
                && System.Testing.DiagnosticHasEndLocation(diagnostics[0]);
        }

        finite law bool DiagnosticDirectLocationAssertionsWork(borrow System.Testing.Diagnostic[] diagnostics)
        {
            return System.Testing.DiagnosticFilePath("Demo.stark", diagnostics[0])
                && System.Testing.DiagnosticAt(diagnostics[0], 7, 13)
                && System.Testing.DiagnosticEndsAt(diagnostics[0], 7, 20);
        }

        finite law bool DiagnosticDirectMessageAssertionsWork(borrow System.Testing.Diagnostic[] diagnostics)
        {
            return System.Testing.DiagnosticMessageEqual("Unknown type 'Missing'.", diagnostics[0])
                && !System.Testing.DiagnosticHasLocation(diagnostics[1]);
        }

        finite law bool DiagnosticDirectChainAssertionsWork(borrow System.Testing.Diagnostic[] diagnostics)
        {
            return System.Testing.DiagnosticHasLocation(diagnostics[0])
                && System.Testing.DiagnosticHasEndLocation(diagnostics[0])
                && System.Testing.DiagnosticFilePath("Demo.stark", diagnostics[0])
                && System.Testing.DiagnosticAt(diagnostics[0], 7, 13)
                && System.Testing.DiagnosticEndsAt(diagnostics[0], 7, 20)
                && System.Testing.DiagnosticMessageEqual("Unknown type 'Missing'.", diagnostics[0])
                && !System.Testing.DiagnosticHasLocation(diagnostics[1]);
        }

        finite law bool TypeAssertionsWork()
        {
            return System.Testing.TypeIs<System.Option<i32[min max]>, System.Core.Option<i32[min max]>>()
                && System.Testing.TypeIsInteger<i32[min max]>()
                && System.Testing.TypeIsStruct<AssertionBox>()
                && System.Testing.TypeIsEnum<AssertionToken>()
                && System.Testing.TypeDisplayName<AssertionBox>("AssertionBox")
                && System.Testing.TypeBaseName<AssertionBox>("AssertionBox")
                && System.Testing.TypeModuleName<AssertionBox>("DemoTests")
                && System.Testing.TypeHasConcreteLayout<AssertionBox>()
                && System.Testing.TypeIsZeroSized<EmptyMarker>()
                && System.Testing.TypeSizeIs<i32[min max]>(4)
                && System.Testing.TypeAlignIs<i32[min max]>(4)
                && System.Testing.TypeIsGenericInstantiation<System.Collections.List<i32[min max]>>()
                && System.Testing.TypeArgumentCount<System.Collections.List<i32[min max]>>(1)
                && System.Testing.TypeComptimeArgumentCount<System.Collections.List<i32[min max]>>(0);
        }

        [Fact]
        finite law bool DiagnosticsAndTypesWork()
        {
            stack System.Testing.Diagnostic[2] diagnostics =
            {
                new System.Testing.Diagnostic()
                {
                    Code = "STK3004",
                    Severity = System.Testing.DiagnosticSeverity.Error,
                    Message = "Unknown type 'Missing'.",
                    Stage = "type-check",
                    Location = new System.Testing.DiagnosticLocation()
                    {
                        HasValue = true,
                        FilePath = "Demo.stark",
                        Line = 7,
                        Column = 13,
                        HasEndValue = true,
                        EndLine = 7,
                        EndColumn = 20
                    }
                },
                new System.Testing.Diagnostic()
                {
                    Code = "STK9000",
                    Severity = System.Testing.DiagnosticSeverity.Warning,
                    Message = "Skipping pass after diagnostics.",
                    Stage = "pipeline",
                    Location = new System.Testing.DiagnosticLocation()
                    {
                        HasValue = false,
                        FilePath = "",
                        Line = 0,
                        Column = 0,
                        HasEndValue = false,
                        EndLine = 0,
                        EndColumn = 0
                    }
                }
            };
            stack System.Testing.Diagnostic[] diagnosticSlice = diagnostics;

            if (!DiagnosticCountAssertionsWork(diagnosticSlice))
            {
                return false;
            }

            if (!DiagnosticContainAssertionsWork(diagnosticSlice))
            {
                return false;
            }

            if (!DiagnosticDirectChainAssertionsWork(diagnosticSlice))
            {
                return false;
            }

            if (!DiagnosticDirectBoolAssertionsWork(diagnosticSlice))
            {
                return false;
            }

            if (!DiagnosticDirectLocationAssertionsWork(diagnosticSlice))
            {
                return false;
            }

            if (!DiagnosticDirectMessageAssertionsWork(diagnosticSlice))
            {
                return false;
            }

            return TypeAssertionsWork();
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

        [Fact]
        fn bool ProcessHarnessWorks()
        {
            unsafe
            {
                stack System.Process.ProcessResult<System.Process.ProcessCommand> commandResult =
                    System.Process.Command("/bin/sh");
                switch (commandResult)
                {
                    case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                        return false;
                    case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var command):
                        stack mut System.Process.ProcessCommand child = command;
                        switch (child.AddArgument("-c"))
                        {
                            case System.Process.ProcessStatus.Err(var error):
                                return false;
                            case System.Process.ProcessStatus.Ok:
                        }

                        switch (child.AddArgument("cat; printf err 1>&2; exit 9"))
                        {
                            case System.Process.ProcessStatus.Err(var error):
                                return false;
                            case System.Process.ProcessStatus.Ok:
                        }

                        if (!System.Testing.RunProcessMatchesWithInput(child, "input-ok\n", 9, "input-ok\n", "err"))
                        {
                            return false;
                        }

                        switch (System.Process.RunCaptureWithInput(child, "input-ok\n"))
                        {
                            case System.Process.ProcessResult<System.Process.ProcessOutput>.Err(var error):
                                return false;
                            case System.Process.ProcessResult<System.Process.ProcessOutput>.Ok(var output):
                                if (!(System.Testing.ProcessOutputEqual(9, "input-ok\n", "err", output)
                                    && System.Testing.ProcessExitCode(9, output)
                                    && System.Testing.ProcessStdoutEqual("input-ok\n", output)
                                    && System.Testing.ProcessStderrEqual("err", output)
                                    && System.Testing.ProcessStdoutStartsWith(output, "input")
                                    && System.Testing.ProcessStdoutEndsWith(output, "\n")
                                    && System.Testing.ProcessStdoutContains(output, "ok")
                                    && System.Testing.ProcessStderrContains(output, "rr")
                                    && System.Testing.ProcessStdoutCountOccurrences(output, "input") == 1
                                    && System.Testing.ProcessStdoutOccurrences(1, output, "ok")
                                    && System.Testing.ProcessStdoutOccurrences(0, output, "")
                                    && System.Testing.ProcessStderrCountOccurrences(output, "r") == 2
                                    && System.Testing.ProcessStderrOccurrences(1, output, "rr")
                                    && !System.Testing.ProcessStdoutEmpty(output)
                                    && !System.Testing.ProcessStderrEmpty(output)
                                    && System.Testing.ProcessCompleted(output)
                                    && !System.Testing.ProcessTimedOut(output)))
                                {
                                    return false;
                                }
                        }

                        switch (System.Process.Command("/bin/sh"))
                        {
                            case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                                return false;
                            case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var timeoutCommand):
                                stack mut System.Process.ProcessCommand timeoutChild = timeoutCommand;
                                switch (timeoutChild.AddArgument("-c"))
                                {
                                    case System.Process.ProcessStatus.Err(var error):
                                        return false;
                                    case System.Process.ProcessStatus.Ok:
                                }

                                switch (timeoutChild.AddArgument("exec sleep 5"))
                                {
                                    case System.Process.ProcessStatus.Err(var error):
                                        return false;
                                    case System.Process.ProcessStatus.Ok:
                                }

                                if (!System.Testing.RunProcessTimesOut(timeoutChild, 500))
                                {
                                    return false;
                                }
                        }

                        switch (System.Process.Command("/bin/sh"))
                        {
                            case System.Process.ProcessResult<System.Process.ProcessCommand>.Err(var error):
                                return false;
                            case System.Process.ProcessResult<System.Process.ProcessCommand>.Ok(var timeoutCommand):
                                stack mut System.Process.ProcessCommand timeoutChild = timeoutCommand;
                                switch (timeoutChild.AddArgument("-c"))
                                {
                                    case System.Process.ProcessStatus.Err(var error):
                                        return false;
                                    case System.Process.ProcessStatus.Ok:
                                }

                                switch (timeoutChild.AddArgument("cat; exec sleep 5"))
                                {
                                    case System.Process.ProcessStatus.Err(var error):
                                        return false;
                                    case System.Process.ProcessStatus.Ok:
                                }

                                if (!System.Testing.RunProcessTimesOutWithInput(timeoutChild, "input-ok\n", 500))
                                {
                                    return false;
                                }
                        }

                        return true;
                }
            }
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

            if (System.Testing.RunFact("DiagnosticsAndTypesWork", DiagnosticsAndTypesWork()) != 0)
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

            if (System.Testing.RunFact("ProcessHarnessWorks", ProcessHarnessWorks()) != 0)
            {
                failed = 1;
            }

            System.Testing.SkipFact("SkippedByDesign", "platform gate");

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

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
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
            Assert.Contains("ok ProcessHarnessWorks", execution.Stdout, StringComparison.Ordinal);
            Assert.Contains("ok DiagnosticsAndTypesWork", execution.Stdout, StringComparison.Ordinal);
            Assert.Contains("skipped SkippedByDesign: platform gate", execution.Stdout, StringComparison.Ordinal);
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
        Assert.Contains("public finite law bool Equal(bool expected, bool actual)", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law bool Contains(ascii value, ascii expected)", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law u64[0 2 ** 63 - 1] CountOccurrences(ascii value, ascii needle)", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law bool Occurrences(u64[0 2 ** 63 - 1] expected, ascii value, ascii needle)", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law bool OptionSome<T>(borrow System.Core.Option<T> value)", testingSource, StringComparison.Ordinal);
        Assert.Contains("public enum DiagnosticSeverity", testingSource, StringComparison.Ordinal);
        Assert.Contains("public struct Diagnostic", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law bool DiagnosticsContain(", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law bool TypeIs<TActual, TExpected>()", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law bool ProcessOutputEqual(", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law u64[0 2 ** 63 - 1] ProcessStdoutCountOccurrences(", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law bool ProcessStderrOccurrences(", testingSource, StringComparison.Ordinal);
        Assert.Contains("public finite law SnapshotResult CompareSnapshotText(", testingSource, StringComparison.Ordinal);
        Assert.Contains("public fn bool Fail(ascii message)", testingSource, StringComparison.Ordinal);
        Assert.Contains("public fn u8[0 1] RunFact(ascii name, bool assertion)", testingSource, StringComparison.Ordinal);
        Assert.Contains("public fn u8[0 1] SkipFact(ascii name, ascii reason)", testingSource, StringComparison.Ordinal);
        Assert.DoesNotContain("public fn bool Equal", testingSource, StringComparison.Ordinal);
        Assert.Contains("import System.Testing", systemSource, StringComparison.Ordinal);
        Assert.DoesNotContain("export import System.Testing", systemSource, StringComparison.Ordinal);
        Assert.Contains("finite law bool AdditionWorks()", TestingAssertionsProgram, StringComparison.Ordinal);
        Assert.Contains("finite law bool DiagnosticCountAssertionsWork(", TestingAssertionsProgram, StringComparison.Ordinal);
        Assert.Contains("finite law bool DiagnosticsAndTypesWork()", TestingAssertionsProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("fn bool DiagnosticCountAssertionsWork", TestingAssertionsProgram, StringComparison.Ordinal);
        Assert.Contains("fn bool RichAssertionsWork()", TestingAssertionsProgram, StringComparison.Ordinal);
    }
}
