using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemExperimentalMemoryStandardLibraryTests : StandardLibraryTestSuite
{
    private const string ExperimentalMemoryProgram = """
        import System.Experimental.Memory
        import System.Memory
        module App

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool InitialBytesOk(borrow i8[-128 127][] values) {
            return values[0] == 1 && values[3] == 4;
        }

        fn bool FilledBytesOk(borrow i8[-128 127][] values) {
            return values[4] == 9 && values[7] == 9;
        }

        fn bool OverwrittenBytesOk(borrow i8[-128 127][] values) {
            return values[0] == 7 && values[1] == 7;
        }

        fn bool InitialCodePointsOk(borrow i32[-2147483648 2147483647][] values) {
            return values[0] == 65 && values[7] == 90;
        }

        export ffi fn i32[min max] main() {
            if (!System.Experimental.Memory.SupportsDynamicAllocator(Allocator.Default())) {
                return 1;
            }

            stack Allocator customAllocator = new Allocator() {
                Kind = 1
            };
            if (System.Experimental.Memory.SupportsDynamicAllocator(customAllocator)) {
                return 2;
            }

            stack mut dynamic i8[-128 127] bytes = new();
            stack mut i8[-128 127][4] sourceBytes = { 1, 2, 3, 4 };
            stack i64[0 max] two = 2;
            stack i64[0 max] four = 4;
            stack i64[0 max] eight = 8;
            if (!Ok(System.Experimental.Memory.ReserveBytes(bytes, eight))) {
                return 3;
            }

            if (bytes.Length != 0) {
                return 20;
            }

            if (bytes.Capacity < 8) {
                return 21;
            }

            if (!Ok(System.Experimental.Memory.AppendBytes(bytes, sourceBytes, four))) {
                return 4;
            }

            if (bytes.Length != 4 || !InitialBytesOk(bytes[0, bytes.Length])) {
                return 5;
            }

            if (!Ok(System.Experimental.Memory.AppendFillBytes(bytes, 9, four))) {
                return 6;
            }

            if (bytes.Length != 8 || !FilledBytesOk(bytes[0, bytes.Length])) {
                return 7;
            }

            if (!Ok(System.Experimental.Memory.FillInitializedBytes(bytes[0, two], 7, two))) {
                return 8;
            }

            if (!OverwrittenBytesOk(bytes[0, bytes.Length])) {
                return 9;
            }

            stack i64[0 max] aliasedByteCount = bytes.Length;
            if (!Ok(System.Experimental.Memory.AppendBytes(bytes, bytes[0, aliasedByteCount], aliasedByteCount))) {
                return 10;
            }

            stack i8[-128 127][] aliasedBytes = bytes[0, bytes.Length];
            if (bytes.Length != 16 || aliasedBytes[8] != aliasedBytes[0] || aliasedBytes[15] != aliasedBytes[7]) {
                return 11;
            }

            stack mut dynamic i32[-2147483648 2147483647] codePoints = new();
            stack mut i32[-2147483648 2147483647][4] sourceCodePoints = { 65, 66, 67, 68 };
            if (!Ok(System.Experimental.Memory.ReserveCodePoints(codePoints, eight))) {
                return 13;
            }

            if (codePoints.Length != 0) {
                return 22;
            }

            if (codePoints.Capacity < 8) {
                return 23;
            }

            if (!Ok(System.Experimental.Memory.AppendCodePoints(codePoints, sourceCodePoints, four))) {
                return 14;
            }

            if (!Ok(System.Experimental.Memory.AppendFillCodePoints(codePoints, 90, four))) {
                return 15;
            }

            if (codePoints.Length != 8 || !InitialCodePointsOk(codePoints[0, codePoints.Length])) {
                return 16;
            }

            stack i64[0 max] aliasedCodePointCount = codePoints.Length;
            if (!Ok(System.Experimental.Memory.AppendCodePoints(codePoints, codePoints[0, aliasedCodePointCount], aliasedCodePointCount))) {
                return 17;
            }

            stack i32[-2147483648 2147483647][] aliasedCodePoints = codePoints[0, codePoints.Length];
            if (codePoints.Length != 16 || aliasedCodePoints[8] != aliasedCodePoints[0] || aliasedCodePoints[15] != aliasedCodePoints[7]) {
                return 18;
            }

            stack mut dynamic i8[-128 127] byteMoveBuffer = new();
            if (!Ok(System.Experimental.Memory.ReserveBytes(byteMoveBuffer, eight))) {
                return 24;
            }

            for willexit (stack mut i64[0 max] byteMoveIndex = 0; byteMoveIndex < eight; byteMoveIndex += 1) {
                init byteMoveBuffer[byteMoveBuffer.Length] = (i8[-128 127])(byteMoveIndex + 1);
            }

            if (!Ok(System.Experimental.Memory.CopyBytes(byteMoveBuffer[0, four], byteMoveBuffer[two, four], four))) {
                return 25;
            }

            stack i8[-128 127][] movedBytesAfterCopy = byteMoveBuffer[0, byteMoveBuffer.Length];
            if (movedBytesAfterCopy[2] != 1 || movedBytesAfterCopy[3] != 2 || movedBytesAfterCopy[4] != 3 || movedBytesAfterCopy[5] != 4) {
                return 26;
            }

            if (!Ok(System.Experimental.Memory.MoveBytes(byteMoveBuffer[two, four], byteMoveBuffer[0, four], four))) {
                return 27;
            }

            stack i8[-128 127][] movedBytesAfterMove = byteMoveBuffer[0, byteMoveBuffer.Length];
            if (movedBytesAfterMove[0] != 1 || movedBytesAfterMove[1] != 2 || movedBytesAfterMove[2] != 3 || movedBytesAfterMove[3] != 4) {
                return 28;
            }

            stack mut dynamic i32[-2147483648 2147483647] codePointMoveBuffer = new();
            if (!Ok(System.Experimental.Memory.ReserveCodePoints(codePointMoveBuffer, eight))) {
                return 29;
            }

            for willexit (stack mut i64[0 max] codePointMoveIndex = 0; codePointMoveIndex < eight; codePointMoveIndex += 1) {
                init codePointMoveBuffer[codePointMoveBuffer.Length] =
                    (i32[-2147483648 2147483647])(65 + codePointMoveIndex);
            }

            if (!Ok(System.Experimental.Memory.CopyCodePoints(codePointMoveBuffer[0, four], codePointMoveBuffer[two, four], four))) {
                return 30;
            }

            stack i32[-2147483648 2147483647][] movedCodePointsAfterCopy = codePointMoveBuffer[0, codePointMoveBuffer.Length];
            if (movedCodePointsAfterCopy[2] != 65 || movedCodePointsAfterCopy[3] != 66 || movedCodePointsAfterCopy[4] != 67 || movedCodePointsAfterCopy[5] != 68) {
                return 31;
            }

            if (!Ok(System.Experimental.Memory.MoveCodePoints(codePointMoveBuffer[two, four], codePointMoveBuffer[0, four], four))) {
                return 32;
            }

            stack i32[-2147483648 2147483647][] movedCodePointsAfterMove = codePointMoveBuffer[0, codePointMoveBuffer.Length];
            if (movedCodePointsAfterMove[0] != 65 || movedCodePointsAfterMove[1] != 66 || movedCodePointsAfterMove[2] != 67 || movedCodePointsAfterMove[3] != 68) {
                return 33;
            }

            return 0;
        }
        """;

    private const string ExperimentalMemoryCopyProgram = """
        import System.Experimental.Memory
        import System.Memory
        module CopyApp

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn i64[min max] SumBytes(borrow i8[-128 127][] values, i64[0 max] count) {
            stack mut i64[min max] checksum = 0;
            for willexit (stack mut i64[0 max] index = 0; index < count; index += 1) {
                checksum += (i64[min max])values[index];
            }

            return checksum;
        }

        fn i64[min max] SumCodePoints(borrow i32[-2147483648 2147483647][] values, i64[0 max] count) {
            stack mut i64[min max] checksum = 0;
            for willexit (stack mut i64[0 max] index = 0; index < count; index += 1) {
                checksum += (i64[min max])values[index];
            }

            return checksum;
        }

        export ffi fn i32[min max] main() {
            stack i64[0 max] count = 32;
            stack mut i8[-128 127][32] byteSource = {
                3, 3, 3, 3, 3, 3, 3, 3,
                3, 3, 3, 3, 3, 3, 3, 3,
                3, 3, 3, 3, 3, 3, 3, 3,
                3, 3, 3, 3, 3, 3, 3, 3
            };
            stack mut i8[-128 127][32] byteDestination = {
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0
            };
            stack mut i32[-2147483648 2147483647][32] codePointSource = {
                65, 65, 65, 65, 65, 65, 65, 65,
                65, 65, 65, 65, 65, 65, 65, 65,
                65, 65, 65, 65, 65, 65, 65, 65,
                65, 65, 65, 65, 65, 65, 65, 65
            };
            stack mut i32[-2147483648 2147483647][32] codePointDestination = {
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0
            };

            if (!Ok(System.Experimental.Memory.CopyBytesDisjoint(byteSource, byteDestination, count))) {
                return 1;
            }

            stack mut i64[min max] checksum = SumBytes(byteDestination, count);
            if (!Ok(System.Experimental.Memory.FillInitializedBytes(byteDestination, 7, count))) {
                return 2;
            }
            checksum += SumBytes(byteDestination, count);

            if (!Ok(System.Experimental.Memory.CopyCodePointsDisjoint(codePointSource, codePointDestination, count))) {
                return 3;
            }
            checksum += SumCodePoints(codePointDestination, count);

            if (!Ok(System.Experimental.Memory.FillInitializedCodePoints(codePointDestination, 90, count))) {
                return 4;
            }
            checksum += SumCodePoints(codePointDestination, count);

            if (checksum != 5280) {
                return 5;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceExperimentalMemorySurfaceCompilesAndLowersIntrinsics()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalMemorySurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Experimental.Memory
                import System.Memory
                module Demo

                fn MemoryStatus InitBytes(
                    disjoint borrow i8[-128 127][] source,
                    disjoint init i8[-128 127][] destination,
                    i64[0 max] count) {
                    return System.Experimental.Memory.InitializeBytesDisjoint(source, destination, count);
                }

                fn MemoryStatus FillBytes(init i8[-128 127][] destination, i64[0 max] count) {
                    return System.Experimental.Memory.FillBytes(destination, 1, count);
                }

                fn MemoryStatus AppendBytes(
                    borrow mut dynamic i8[-128 127] destination,
                    borrow i8[-128 127][] source,
                    i64[0 max] count) {
                    return System.Experimental.Memory.AppendBytes(destination, source, count);
                }

                fn MemoryStatus CopyCodePoints(
                    disjoint borrow i32[-2147483648 2147483647][] source,
                    disjoint borrow mut i32[-2147483648 2147483647][] destination,
                    i64[0 max] count) {
                    return System.Experimental.Memory.CopyCodePointsDisjoint(source, destination, count);
                }

                fn MemoryStatus MoveBytes(
                    borrow i8[-128 127][] source,
                    borrow mut i8[-128 127][] destination,
                    i64[0 max] count) {
                    return System.Experimental.Memory.MoveBytes(source, destination, count);
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("System_Experimental_Memory_InitializeBytesDisjoint", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Experimental_Memory_FillBytes", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Experimental_Memory_CopyCodePointsDisjoint", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Experimental_Memory_MoveBytes", llvm, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceExperimentalMemoryModuleLowersRuntimeDisjointAppendFastPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Experimental", "Memory.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O0,
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var appendBytesBody = ExtractLlvmFunctionBody(llvm, "@AppendBytes(");
        var appendCodePointsBody = ExtractLlvmFunctionBody(llvm, "@AppendCodePoints(");
        var copyBytesBody = ExtractLlvmFunctionBody(llvm, "@CopyBytes(");
        var copyCodePointsBody = ExtractLlvmFunctionBody(llvm, "@CopyCodePoints(");

        Assert.Contains("icmp ule ptr", appendBytesBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", appendCodePointsBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", copyBytesBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", copyCodePointsBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibExperimentalMemoryExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(ExperimentalMemoryProgram, "stark-stdlib-experimental-memory-");
    }

    [Fact]
    public async Task SourceStdLibExperimentalMemoryCopyExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(ExperimentalMemoryCopyProgram, "stark-stdlib-experimental-memory-copy-");
    }

    private async Task AssertSourceExecutableRunsAsync(string source, string tempPrefix)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
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

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
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
