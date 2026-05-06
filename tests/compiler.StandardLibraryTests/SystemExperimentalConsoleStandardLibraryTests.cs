using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemExperimentalConsoleStandardLibraryTests : StandardLibraryTestSuite
{
    private const string ExperimentalConsoleProgram = """
        import System.Console
        import System.Runtime.Buffer
        import System.Text
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

        fn bool IsAlpha(System.Memory.MemoryResult<System.Text.OwnedAscii> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                    return false;
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                    stack mut System.Text.OwnedAscii line = value;
                    if (line.Length() != 5) {
                        return false;
                    }

                    stack i8[min max][] slice = line.AsSlice();
                    return slice[0] == 97
                        && slice[1] == 108
                        && slice[2] == 112
                        && slice[3] == 104
                        && slice[4] == 97;
            }
        }

        fn bool IsCafe(System.Memory.MemoryResult<System.Text.OwnedUnicode> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var error):
                    return false;
                case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var value):
                    stack mut System.Text.OwnedUnicode line = value;
                    if (line.Length() != 4) {
                        return false;
                    }

                    stack i32[min max][] slice = line.AsSlice();
                    return slice[0] == 99
                        && slice[1] == 97
                        && slice[2] == 102
                        && slice[3] == 233;
            }
        }

        fn bool IsZ(System.Memory.MemoryResult<System.Text.OwnedUnicode> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var error):
                    return false;
                case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var value):
                    stack mut System.Text.OwnedUnicode unit = value;
                    if (unit.Length() != 1) {
                        return false;
                    }

                    stack i32[min max][] slice = unit.AsSlice();
                    return slice[0] == 90;
            }
        }

        fn bool IsTooLargeAscii(System.Memory.MemoryResult<System.Text.OwnedAscii> result) {
            switch (result) {
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                    return false;
                case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
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

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Text.OwnedAscii owned = new();
            if (!MemoryOk(owned.AppendAscii("owned"))) {
                return 1;
            }

            stack mut i8[min max][3] bufferBytes = { 66, 85, 70 };
            stack mut System.Runtime.Buffer.FixedByteBuffer512 fixedBuffer = new();
            if (!MemoryOk(fixedBuffer.WriteSlice(bufferBytes, 3))) {
                return 2;
            }

            if (!StatusOk(System.Console.Write("ascii:"))
                || !StatusOk(System.Console.WriteLine(owned))
                || !StatusOk(System.Console.Write((unicode)"unicode:"))
                || !StatusOk(System.Console.WriteLine((unicode)"Î±"))
                || !StatusOk(System.Console.WriteLine(fixedBuffer))
                || !StatusOk(System.Console.WriteErrorLine("err"))) {
                return 3;
            }

            if (!IsAlpha(System.Console.ReadAsciiLine())) {
                return 4;
            }

            if (!IsCafe(System.Console.ReadUnicodeLine())) {
                return 5;
            }

            if (!IsZ(System.Console.Read())) {
                return 6;
            }

            stack mut System.Runtime.Buffer.DynamicByteBuffer dynamicBytes = new();
            if (!ReadCount(System.Console.ReadBytes(dynamicBytes, 3), 3)) {
                return 7;
            }

            stack i8[min max][] readBytes = dynamicBytes.ReadSlice();
            if (readBytes[0] != 49 || readBytes[1] != 50 || readBytes[2] != 51) {
                return 8;
            }

            if (!IsTooLargeAscii(System.Console.ReadAsciiLine())) {
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
                import System.Console
                import System.Runtime.Buffer
                import System.Text
                import System.IO
                import System.Memory
                module Demo

                fn System.IO.IOStatus WriteOwned(mut borrow System.Text.OwnedAscii text) {
                    return System.Console.WriteLine(text);
                }

                fn System.IO.IOStatus WriteBuffer(borrow System.Runtime.Buffer.DynamicByteBuffer buffer) {
                    return System.Console.Write(buffer);
                }

                fn System.Memory.MemoryResult<u64[0 2 ** 63 - 1]> ReadInto(
                    mut borrow System.Runtime.Buffer.FixedByteBuffer8192 buffer) {
                    return System.Console.ReadBytes(buffer, 32);
                }

                fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadLine() {
                    return System.Console.ReadLine();
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("System_Console", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Buffer_FixedByteBuffer8192_WriteByte", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Experimental_Console", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceExperimentalConsoleByteAndLineWritesUseDirectPlatformPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Console.stark");
        var platformPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform.stark");
        var resolver = new TargetAwareStdLibModuleResolver(
            new FileSystemModuleResolver(sourceRoot),
            [sourceRoot],
            targetInfo: null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O3,
                EmitLlvmIr: true,
                ModuleResolver: resolver));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        var writeBytesToStdout = ExtractLlvmFunctionBody(llvm, "@WriteBytesToStdout(");
        var writeBytesToStderr = ExtractLlvmFunctionBody(llvm, "@WriteBytesToStderr(");
        var writeBytesLineToStdout = ExtractLlvmFunctionBody(llvm, "@WriteBytesLineToStdout(");
        var writeBytesLineToStderr = ExtractLlvmFunctionBody(llvm, "@WriteBytesLineToStderr(");
        var writeLineAscii = ExtractLlvmFunctionBody(llvm, "@WriteLine__ascii_(");
        var writeLineUnicode = ExtractLlvmFunctionBody(llvm, "@WriteLine__unicode_(");
        var writeLineBytes = ExtractLlvmFunctionBody(llvm, "@WriteLine__borrowi8_minmax____(");
        var writeErrorLineAscii = ExtractLlvmFunctionBody(llvm, "@WriteErrorLine__ascii_(");
        var writeErrorLineUnicode = ExtractLlvmFunctionBody(llvm, "@WriteErrorLine__unicode_(");
        var writeErrorLineBytes = ExtractLlvmFunctionBody(llvm, "@WriteErrorLine__borrowi8_minmax____(");
        var byteWriteBodies = string.Join(
            Environment.NewLine,
            [writeBytesToStdout, writeBytesToStderr, writeBytesLineToStdout, writeBytesLineToStderr]);

        Assert.Contains("@System_Runtime_Platform_WriteStdoutBytes(", writeBytesToStdout, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_WriteStderrBytes(", writeBytesToStderr, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_WriteStdoutBytesLine(", writeBytesLineToStdout, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_WriteStderrBytesLine(", writeBytesLineToStderr, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_WriteStdoutAsciiLine(", writeLineAscii, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_WriteStdoutUnicodeLine(", writeLineUnicode, StringComparison.Ordinal);
        Assert.Contains("@WriteBytesLineToStdout(", writeLineBytes, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_WriteStderrAsciiLine(", writeErrorLineAscii, StringComparison.Ordinal);
        Assert.Contains("@System_Runtime_Platform_WriteStderrUnicodeLine(", writeErrorLineUnicode, StringComparison.Ordinal);
        Assert.Contains("@WriteBytesLineToStderr(", writeErrorLineBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Text_OwnedAscii_AppendByte", byteWriteBodies, StringComparison.Ordinal);
        Assert.DoesNotContain("@Write__ascii_", writeLineAscii, StringComparison.Ordinal);
        Assert.DoesNotContain("@WriteError__ascii_", writeErrorLineAscii, StringComparison.Ordinal);

        var platformSource = File.ReadAllText(platformPath);
        Assert.Contains("WriteStdoutBytes(rawptr<i8[min max]>[length] data, u64[0 2 ** 63 - 1] length)", platformSource, StringComparison.Ordinal);
        Assert.Contains("WriteStderrBytes(rawptr<i8[min max]>[length] data, u64[0 2 ** 63 - 1] length)", platformSource, StringComparison.Ordinal);
        Assert.Contains("ReadStdin(rawmutptr<i8[min max]>[capacity] buffer, u64[0 2 ** 63 - 1] capacity)", platformSource, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceRuntimePlatformLineWritesCoalesceSmallBuffers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O3,
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        var bufferLine = ExtractLlvmFunctionBody(llvm, "@WriteBufferLineToHandle(");
        var asciiLine = ExtractLlvmFunctionBody(llvm, "@WriteAsciiLineToHandle(");
        var unicodeLine = ExtractLlvmFunctionBody(llvm, "@WriteUnicodeLineToHandle(");

        Assert.Contains("%System_Runtime_Buffer_FixedByteBuffer8192", bufferLine, StringComparison.Ordinal);
        Assert.Contains("@WriteBufferToHandle(", bufferLine, StringComparison.Ordinal);
        Assert.Contains("@WriteBufferLineToHandle(", asciiLine, StringComparison.Ordinal);
        Assert.Contains("%System_Runtime_Buffer_FixedByteBuffer4096", unicodeLine, StringComparison.Ordinal);
        Assert.Contains("@AppendUtf8CodePoint(", unicodeLine, StringComparison.Ordinal);
        Assert.Contains("i32 10", unicodeLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibExperimentalConsoleExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(
            ExperimentalConsoleProgram,
            "stark-stdlib-experimental-console-",
            "alpha\r\ncafÃ©\nZ123" + new string('a', 8193),
            "ascii:owned\nunicode:Î±\nBUF\n",
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

    private static string ExtractLlvmFunctionBody(string llvm, string functionSignatureFragment)
    {
        var signatureIndex = llvm.IndexOf(functionSignatureFragment, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Unable to find LLVM function fragment '{functionSignatureFragment}'.");

        var start = llvm.LastIndexOf("\ndefine ", signatureIndex, StringComparison.Ordinal);
        if (start < 0)
        {
            start = llvm.LastIndexOf("define ", signatureIndex, StringComparison.Ordinal);
        }

        Assert.True(start >= 0, $"Unable to find LLVM function start for '{functionSignatureFragment}'.");

        var next = llvm.IndexOf("\ndefine ", signatureIndex + functionSignatureFragment.Length, StringComparison.Ordinal);
        if (next < 0)
        {
            next = llvm.Length;
        }

        return llvm[start..next];
    }
}

