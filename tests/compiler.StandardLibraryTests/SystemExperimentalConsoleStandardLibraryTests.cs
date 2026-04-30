using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemExperimentalConsoleStandardLibraryTests : StandardLibraryTestSuite
{
    private const string ExperimentalConsoleProgram = """
        import System.Experimental.Console
        import System.Experimental.Runtime.Buffer
        import System.Experimental.Text
        import System.IO
        import System.Memory
        module App

        fn bool StatusOk(System.IO.IOStatus status) {
            switch (status) {
                case System.IO.IOStatus.Ok:
                    return true;
                case System.IO.IOStatus.Err(var error):
                    return false;
            }
        }

        fn bool MemoryOk(System.Memory.MemoryStatus status) {
            switch (status) {
                case System.Memory.MemoryStatus.Ok:
                    return true;
                case System.Memory.MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool ReadCount(System.Memory.MemoryResult<i64[0 max]> result, i64[0 max] expected) {
            switch (result) {
                case System.Memory.MemoryResult<i64[0 max]>.Ok(var value):
                    return value == expected;
                case System.Memory.MemoryResult<i64[0 max]>.Err(var error):
                    return false;
            }
        }

        fn bool IsAlpha(System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var error):
                    return false;
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var value):
                    stack mut System.Experimental.Text.OwnedAscii line = value;
                    if (line.Length() != 5) {
                        return false;
                    }

                    stack i8[-128 127][] slice = line.AsSlice();
                    return slice[0] == 97
                        && slice[1] == 108
                        && slice[2] == 112
                        && slice[3] == 104
                        && slice[4] == 97;
            }
        }

        fn bool IsCafe(System.Memory.MemoryResult<System.Experimental.Text.OwnedUnicode> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedUnicode>.Err(var error):
                    return false;
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedUnicode>.Ok(var value):
                    stack mut System.Experimental.Text.OwnedUnicode line = value;
                    if (line.Length() != 4) {
                        return false;
                    }

                    stack i32[-2147483648 2147483647][] slice = line.AsSlice();
                    return slice[0] == 99
                        && slice[1] == 97
                        && slice[2] == 102
                        && slice[3] == 233;
            }
        }

        fn bool IsZ(System.Memory.MemoryResult<System.Experimental.Text.OwnedUnicode> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedUnicode>.Err(var error):
                    return false;
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedUnicode>.Ok(var value):
                    stack mut System.Experimental.Text.OwnedUnicode unit = value;
                    if (unit.Length() != 1) {
                        return false;
                    }

                    stack i32[-2147483648 2147483647][] slice = unit.AsSlice();
                    return slice[0] == 90;
            }
        }

        fn bool IsTooLargeAscii(System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var value):
                    return false;
                case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var error):
                    switch (error) {
                        case System.Memory.MemoryError.OutOfMemory:
                            return false;
                        case System.Memory.MemoryError.TooLarge:
                            return true;
                        case System.Memory.MemoryError.InvalidLayout:
                            return false;
                        case System.Memory.MemoryError.UnsupportedAlignment:
                            return false;
                    }
            }
        }

        export ffi fn i32[min max] main() {
            stack mut System.Experimental.Text.OwnedAscii owned = new();
            if (!MemoryOk(owned.AppendAscii("owned"))) {
                return 1;
            }

            stack mut i8[-128 127][3] bufferBytes = { 66, 85, 70 };
            stack mut System.Experimental.Runtime.Buffer.FixedByteBuffer512 fixedBuffer = new();
            if (!MemoryOk(fixedBuffer.WriteSlice(bufferBytes, 3))) {
                return 2;
            }

            if (!StatusOk(System.Experimental.Console.Write("ascii:"))
                || !StatusOk(System.Experimental.Console.WriteLine(owned))
                || !StatusOk(System.Experimental.Console.Write((unicode)"unicode:"))
                || !StatusOk(System.Experimental.Console.WriteLine((unicode)"α"))
                || !StatusOk(System.Experimental.Console.WriteLine(fixedBuffer))
                || !StatusOk(System.Experimental.Console.WriteErrorLine("err"))) {
                return 3;
            }

            if (!IsAlpha(System.Experimental.Console.ReadAsciiLine())) {
                return 4;
            }

            if (!IsCafe(System.Experimental.Console.ReadUnicodeLine())) {
                return 5;
            }

            if (!IsZ(System.Experimental.Console.Read())) {
                return 6;
            }

            stack mut System.Experimental.Runtime.Buffer.DynamicByteBuffer dynamicBytes = new();
            if (!ReadCount(System.Experimental.Console.ReadBytes(dynamicBytes, 3), 3)) {
                return 7;
            }

            stack i8[-128 127][] readBytes = dynamicBytes.ReadSlice();
            if (readBytes[0] != 49 || readBytes[1] != 50 || readBytes[2] != 51) {
                return 8;
            }

            if (!IsTooLargeAscii(System.Experimental.Console.ReadAsciiLine())) {
                return 9;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceExperimentalConsoleSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalConsoleSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Experimental.Console
                import System.Experimental.Runtime.Buffer
                import System.Experimental.Text
                import System.IO
                import System.Memory
                module Demo

                fn System.IO.IOStatus WriteOwned(mut borrow System.Experimental.Text.OwnedAscii text) {
                    return System.Experimental.Console.WriteLine(text);
                }

                fn System.IO.IOStatus WriteBuffer(borrow System.Experimental.Runtime.Buffer.DynamicByteBuffer buffer) {
                    return System.Experimental.Console.Write(buffer);
                }

                fn System.Memory.MemoryResult<i64[0 max]> ReadInto(
                    mut borrow System.Experimental.Runtime.Buffer.FixedByteBuffer8192 buffer) {
                    return System.Experimental.Console.ReadBytes(buffer, 32);
                }

                fn System.Memory.MemoryResult<System.Experimental.Text.OwnedUnicode> ReadLine() {
                    return System.Experimental.Console.ReadLine();
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("System_Experimental_Console", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Experimental_Runtime_Buffer_FixedByteBuffer8192_WriteByte", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Console_", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibExperimentalConsoleExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(
            ExperimentalConsoleProgram,
            "stark-stdlib-experimental-console-",
            "alpha\r\ncafé\nZ123" + new string('a', 8193),
            "ascii:owned\nunicode:α\nBUF\n",
            "err\n",
            skipWindows: true);
    }

    private async Task AssertSourceExecutableRunsAsync(
        string source,
        string tempPrefix,
        string stdin,
        string expectedStdout,
        string expectedStderr,
        bool skipWindows)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || (skipWindows && OperatingSystem.IsWindows()))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory(tempPrefix);
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, source);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, stdin);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(expectedStdout, execution.Stdout);
            Assert.Equal(expectedStderr, execution.Stderr);
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
}
