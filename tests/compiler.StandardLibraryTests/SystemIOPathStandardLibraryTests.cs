using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOPathStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourcePromotedPathLowersThroughDynamicStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedPathLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.IO.Path
                import System.Text
                import System.Memory
                module Demo

                fn bool Ok(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn u64[0 2 ** 63 - 1] BuildAndInspect() {
                    stack mut System.Text.OwnedAscii path = new();
                    if (!Ok(System.IO.Path.TryJoin(path, "alpha/", "/beta.txt"))) {
                        return 0;
                    }

                    stack ascii view = path.View();
                    stack System.IO.Path.PathFacts facts = System.IO.Path.GetFacts(view);
                    return (u64[0 2 ** 63 - 1])(path.Length()
                        + facts.ExtensionLength()
                        + facts.BaseNameLength()
                        + facts.DirectoryNameLength());
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("@__stark_runtime_try_realloc", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("extractvalue { ptr, i64, i64 }", llvm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedPathTryJoinUsesTailRegionPointerCopies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "IO", "Path.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                EmitLlvmIr: true,
                OptimizationLevel: CompilerOptimizationLevel.O3));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var tryJoinBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryJoin(",
            "Expected promoted TryJoin definition in path module.");
        var tryJoinPointerRangesBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryJoinPointerRanges(",
            "Expected promoted TryJoinPointerRanges definition in path module.");
        var tryJoinConstBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryJoinConst(",
            "Expected promoted TryJoinConst definition in path module.");
        var tryNormalizeBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryNormalizeSeparators(",
            "Expected promoted TryNormalizeSeparators definition in path module.");
        var tryNormalizeConstBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryNormalizeSeparatorsConst(",
            "Expected promoted TryNormalizeSeparatorsConst definition in path module.");
        var getConstFactsBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc void @GetConstFacts(",
            "Expected promoted GetConstFacts definition in path module.");

        Assert.Contains("@TryJoinPointerRanges", tryJoinBody, StringComparison.Ordinal);
        Assert.Contains("@TryJoinPointerRanges", tryJoinConstBody, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(tryJoinPointerRangesBody, "@llvm.memcpy.p0.p0.i64") >= 2,
            "Expected TryJoin pointer core to copy left and right path ranges through direct tail-region memcpy operations.");
        Assert.DoesNotContain("@System_Text_OwnedAscii_AppendAscii", tryJoinConstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Text_OwnedAscii_AppendAscii", tryNormalizeConstBody, StringComparison.Ordinal);
        Assert.Contains("@AppendNormalizedSeparatorsCore", tryNormalizeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@AppendNormalizedSeparators(", tryNormalizeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", tryJoinConstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", tryNormalizeConstBody, StringComparison.Ordinal);
        Assert.Contains("@System_Text_AsciiLength", getConstFactsBody, StringComparison.Ordinal);
        Assert.Contains("@System_Text_AsciiData", getConstFactsBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedPathCorrectnessSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedPathCorrectness.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.IO.Path
                import System.Text
                import System.Memory
                module Demo

                const ascii ConstLeft = "alpha";
                const ascii ConstRight = "beta.txt";
                const ascii ConstNormalizedSource = "alpha//beta///gamma.txt";

                fn bool Ok(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool IsJoinedPath(ascii value) {
                    switch (value) {
                        case "alpha/beta.txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsNormalizedPath(ascii value) {
                    switch (value) {
                        case "alpha/beta/gamma.txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsTextExtension(ascii value) {
                    switch (value) {
                        case ".txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsBetaBaseName(ascii value) {
                    switch (value) {
                        case "beta":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsAlphaDirectory(ascii value) {
                    switch (value) {
                        case "alpha":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsOwnedJoinedPath(System.Memory.MemoryResult<System.Text.OwnedAscii> result) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == 14 && IsJoinedPath(value.View());
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                fn bool IsOwnedNormalizedPath(System.Memory.MemoryResult<System.Text.OwnedAscii> result) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == 20 && IsNormalizedPath(value.View());
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                fn bool Probe() {
                    stack mut System.Text.OwnedAscii joined = new();
                    if (!Ok(System.IO.Path.TryJoin(joined, "alpha/", "/beta.txt"))) {
                        return false;
                    }

                    stack ascii joinedView = joined.View();
                    if (!IsJoinedPath(joinedView)) {
                        return false;
                    }

                    stack System.IO.Path.PathFacts facts = System.IO.Path.GetFacts(joinedView);
                    if (!IsTextExtension(facts.Extension()) || !IsBetaBaseName(facts.BaseName()) || !IsAlphaDirectory(facts.DirectoryName())) {
                        return false;
                    }

                    if (facts.PathLength() != 14 || facts.ExtensionLength() != 4 || facts.BaseNameLength() != 4 || facts.DirectoryNameLength() != 5) {
                        return false;
                    }

                    stack mut System.Text.OwnedAscii normalized = new();
                    if (!Ok(System.IO.Path.TryNormalizeSeparators(normalized, "alpha//beta///gamma.txt"))) {
                        return false;
                    }

                    stack System.IO.Path.PathFacts constFacts = System.IO.Path.GetConstFacts("alpha/beta.txt");
                    if (!IsTextExtension(constFacts.Extension())
                        || !IsBetaBaseName(System.IO.Path.BaseNameConst("alpha/beta.txt"))
                        || !IsAlphaDirectory(System.IO.Path.DirectoryNameConst("alpha/beta.txt"))) {
                        return false;
                    }

                    stack mut System.Text.OwnedAscii constJoined = new();
                    if (!Ok(System.IO.Path.TryJoinConst(constJoined, ConstLeft, ConstRight)) || !IsJoinedPath(constJoined.View())) {
                        return false;
                    }

                    stack mut System.Text.OwnedAscii constNormalized = new();
                    if (!Ok(System.IO.Path.TryNormalizeSeparatorsConst(constNormalized, ConstNormalizedSource)) || !IsNormalizedPath(constNormalized.View())) {
                        return false;
                    }

                    return IsNormalizedPath(normalized.View())
                        && IsOwnedJoinedPath(System.IO.Path.Join("alpha", "beta.txt"))
                        && IsOwnedJoinedPath(System.IO.Path.JoinConst(ConstLeft, ConstRight))
                        && IsOwnedJoinedPath(System.IO.Path.Join("alpha/", "/beta.txt"))
                        && IsOwnedNormalizedPath(System.IO.Path.NormalizeSeparators("alpha//beta///gamma.txt"))
                        && IsOwnedNormalizedPath(System.IO.Path.NormalizeSeparatorsConst(ConstNormalizedSource));
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibPromotedPathCorrectnessExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-path-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.IO.Path
                import System.Text
                import System.Memory
                module App

                const ascii ConstLeft = "alpha";
                const ascii ConstRight = "beta.txt";
                const ascii ConstNormalizedSource = "alpha//beta///gamma.txt";

                fn bool Ok(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool IsJoinedPath(ascii value) {
                    switch (value) {
                        case "alpha/beta.txt":
                            return true;
                        case "alpha\\beta.txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsNormalizedPath(ascii value) {
                    switch (value) {
                        case "alpha/beta/gamma.txt":
                            return true;
                        case "alpha\\beta\\gamma.txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsSelfJoinedPath(ascii value) {
                    switch (value) {
                        case "alpha/alpha":
                            return true;
                        case "alpha\\alpha":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsTextExtension(ascii value) {
                    switch (value) {
                        case ".txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsGzExtension(ascii value) {
                    switch (value) {
                        case ".gz":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsBetaBaseName(ascii value) {
                    switch (value) {
                        case "beta":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsArchiveBaseName(ascii value) {
                    switch (value) {
                        case "archive.tar":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsHiddenBaseName(ascii value) {
                    switch (value) {
                        case ".hidden":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsAlphaDirectory(ascii value) {
                    switch (value) {
                        case "alpha":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsEmpty(ascii value) {
                    return System.Text.AsciiLength(value) == 0;
                }

                fn bool CheckFacts() {
                    stack System.IO.Path.PathFacts facts = System.IO.Path.GetFacts("alpha/beta.txt");
                    if (facts.PathLength() != 14
                        || facts.ExtensionLength() != 4
                        || facts.BaseNameLength() != 4
                        || facts.DirectoryNameLength() != 5
                        || !IsTextExtension(facts.Extension())
                        || !IsBetaBaseName(facts.BaseName())
                        || !IsAlphaDirectory(facts.DirectoryName())) {
                        return false;
                    }

                    stack System.IO.Path.PathFacts constFacts = System.IO.Path.GetConstFacts("alpha/beta.txt");
                    if (constFacts.PathLength() != 14
                        || constFacts.ExtensionLength() != 4
                        || constFacts.BaseNameLength() != 4
                        || constFacts.DirectoryNameLength() != 5
                        || !IsTextExtension(System.IO.Path.ExtensionConst("alpha/beta.txt"))
                        || !IsBetaBaseName(System.IO.Path.BaseNameConst("alpha/beta.txt"))
                        || !IsAlphaDirectory(System.IO.Path.DirectoryNameConst("alpha/beta.txt"))) {
                        return false;
                    }

                    stack System.IO.Path.PathFacts archive = System.IO.Path.GetFacts("archive.tar.gz");
                    if (!IsGzExtension(archive.Extension()) || !IsArchiveBaseName(archive.BaseName()) || archive.DirectoryNameLength() != 0) {
                        return false;
                    }

                    stack System.IO.Path.PathFacts hidden = System.IO.Path.GetFacts("alpha/.hidden");
                    if (!IsEmpty(hidden.Extension()) || !IsHiddenBaseName(hidden.BaseName()) || !IsAlphaDirectory(hidden.DirectoryName())) {
                        return false;
                    }

                    stack System.IO.Path.PathFacts root = System.IO.Path.GetFacts("/");
                    return root.PathLength() == 1
                        && root.DirectoryNameLength() == 1
                        && root.BaseNameLength() == 0
                        && IsEmpty(System.IO.Path.GetFacts("").BaseName())
                        && IsEmpty(System.IO.Path.Extension(".gitignore"))
                        && IsHiddenBaseName(System.IO.Path.BaseName("alpha/.hidden"));
                }

                fn i32[min max] CheckJoinAndNormalize() {
                    stack mut System.Text.OwnedAscii joined = new();
                    if (!Ok(System.IO.Path.TryJoin(joined, "alpha/", "/beta.txt"))) {
                        return 1;
                    }

                    if (System.Text.AsciiLength(joined.View()) != 14) {
                        return 7;
                    }

                    if (!IsJoinedPath(joined.View())) {
                        return 9;
                    }

                    stack mut System.Text.OwnedAscii normalized = new();
                    if (!Ok(System.IO.Path.TryNormalizeSeparators(normalized, "alpha//beta///gamma.txt")) || !IsNormalizedPath(normalized.View())) {
                        return 2;
                    }

                    stack mut System.Text.OwnedAscii constJoined = new();
                    if (!Ok(System.IO.Path.TryJoinConst(constJoined, ConstLeft, ConstRight)) || !IsJoinedPath(constJoined.View())) {
                        return 10;
                    }

                    stack mut System.Text.OwnedAscii constNormalized = new();
                    if (!Ok(System.IO.Path.TryNormalizeSeparatorsConst(constNormalized, ConstNormalizedSource)) || !IsNormalizedPath(constNormalized.View())) {
                        return 11;
                    }

                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> ownedJoin = System.IO.Path.Join("alpha", "beta.txt");
                    switch (ownedJoin) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var ownedJoinValue):
                            if (!IsJoinedPath(ownedJoinValue.View())) {
                                return 3;
                            }
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var ownedJoinError):
                            return 4;
                    }

                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> ownedConstJoin = System.IO.Path.JoinConst(ConstLeft, ConstRight);
                    switch (ownedConstJoin) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var ownedConstJoinValue):
                            if (!IsJoinedPath(ownedConstJoinValue.View())) {
                                return 12;
                            }
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var ownedConstJoinError):
                            return 13;
                    }

                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> ownedNormalize = System.IO.Path.NormalizeSeparators("alpha//beta///gamma.txt");
                    switch (ownedNormalize) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var ownedNormalizeValue):
                            if (!IsNormalizedPath(ownedNormalizeValue.View())) {
                                return 5;
                            }
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var ownedNormalizeError):
                            return 6;
                    }

                    stack System.Memory.MemoryResult<System.Text.OwnedAscii> ownedConstNormalize = System.IO.Path.NormalizeSeparatorsConst(ConstNormalizedSource);
                    switch (ownedConstNormalize) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var ownedConstNormalizeValue):
                            if (!IsNormalizedPath(ownedConstNormalizeValue.View())) {
                                return 14;
                            }
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var ownedConstNormalizeError):
                            return 15;
                    }

                    return 0;
                }

                fn bool CheckSelfViewAliases() {
                    stack mut System.Text.OwnedAscii selfJoin = new();
                    if (!Ok(selfJoin.AppendAscii("alpha"))) {
                        return false;
                    }

                    stack ascii sameView = selfJoin.View();
                    if (!Ok(System.IO.Path.TryJoin(selfJoin, sameView, sameView))) {
                        return false;
                    }

                    if (System.Text.AsciiLength(selfJoin.View()) != 11
                        || !IsSelfJoinedPath(selfJoin.View())) {
                        return false;
                    }

                    stack mut System.Text.OwnedAscii rightAlias = new();
                    if (!Ok(rightAlias.AppendAscii("beta.txt"))) {
                        return false;
                    }

                    stack ascii rightView = rightAlias.View();
                    if (!Ok(System.IO.Path.TryJoin(rightAlias, "alpha", rightView)) || !IsJoinedPath(rightAlias.View())) {
                        return false;
                    }

                    stack mut System.Text.OwnedAscii normalizeAlias = new();
                    if (!Ok(normalizeAlias.AppendAscii("alpha//beta///gamma.txt"))) {
                        return false;
                    }

                    stack ascii normalizeView = normalizeAlias.View();
                    return Ok(System.IO.Path.TryNormalizeSeparators(normalizeAlias, normalizeView))
                        && IsNormalizedPath(normalizeAlias.View());
                }

                unsafe fn i32[min max] CheckTooLargeNormalization() {
                    stack mut i8[min max][1] storage = { 47 };
                    stack Ascii huge = new Ascii() {
                        Data = &storage[0],
                        Length = (i64[min max])(2 ** 63 - 1),
                        Capacity = (i64[min max])(2 ** 63 - 1)
                    };
                    stack mut System.Text.OwnedAscii destination = new();
                    stack ascii hugeView = System.Text.AsciiView(huge);
                    if (System.Text.AsciiLength(hugeView) == 0) {
                        return 1;
                    }

                    stack System.Memory.MemoryStatus status = System.IO.Path.TryNormalizeSeparators(destination, hugeView);
                    if (Ok(status)) {
                        return 2;
                    }

                    if (destination.Length() != 0) {
                        return 3;
                    }

                    return 0;
                }

                export unsafe fn i32[min max] main() {
                    if (!CheckFacts()) {
                        return 1;
                    }

                    stack i32[min max] joinAndNormalize = CheckJoinAndNormalize();
                    if (joinAndNormalize != 0) {
                        return 20 + joinAndNormalize;
                    }

                    if (!CheckSelfViewAliases()) {
                        return 3;
                    }

                    stack i32[min max] tooLarge = CheckTooLargeNormalization();
                    if (tooLarge != 0) {
                        return 40 + tooLarge;
                    }

                    return 0;
                }
                """);

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

    [Fact]
    public async Task PackagedStdLibPathCurrentDirectoryFillsCallerProvidedAsciiBuffer()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-current-directory-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");
        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                import System.Text
                module App

                fn bool StatusOk(System.IO.IOStatus status) {
                    switch (status) {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result) {
                    switch (result) {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
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

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result) {
                    switch (result) {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }

                export fn i32[min max] main() {
                    stack mut System.Text.OwnedAscii owned = new();

                    if (!MemoryOk(System.IO.Path.CurrentDirectory(owned))) {
                        return 1;
                    }

                    if (owned.Length() <= 0) {
                        return 2;
                    }

                    stack System.IO.IOStatus status = System.Console.WriteLine(owned.View());
                    switch (status) {
                        case System.IO.IOStatus.Ok:
                            return 0;
                        case System.IO.IOStatus.Err(var error):
                            return 3;
                    }

                    return 4;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = appDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(appDirectory + "\n", processStdout);
            Assert.Equal(string.Empty, processStderr);
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
    public async Task PackagedStdLibPathHelpersWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || OperatingSystem.IsWindows()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-path-helpers-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app");
        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                import System.Text
                module App

                fn bool MemoryOk(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool IsJoinedPath(ascii value) {
                    switch (value) {
                        case "alpha/beta.txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsTextExtension(ascii value) {
                    switch (value) {
                        case ".txt":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsBetaBaseName(ascii value) {
                    switch (value) {
                        case "beta":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsAlphaDirectory(ascii value) {
                    switch (value) {
                        case "alpha":
                            return true;
                        default:
                            return false;
                    }
                }

                fn bool IsOwnedJoinedPath(System.Memory.MemoryResult<System.Text.OwnedAscii> result) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == 14 && IsJoinedPath(value.View());
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                export unsafe fn i32[min max] main() {
                    stack mut System.Text.OwnedAscii joined = new();

                    if (!MemoryOk(System.IO.Path.TryJoin(joined, "alpha", "beta.txt"))) {
                        return 1;
                    }

                    if (!IsJoinedPath(joined.View())) {
                        return 2;
                    }

                    if (!IsTextExtension(System.IO.Path.Extension(joined.View()))) {
                        return 3;
                    }

                    if (!IsBetaBaseName(System.IO.Path.BaseName(joined.View()))) {
                        return 4;
                    }

                    if (!IsAlphaDirectory(System.IO.Path.DirectoryName(joined.View()))) {
                        return 5;
                    }

                    stack System.IO.Path.PathFacts facts = System.IO.Path.GetFacts(joined.View());
                    if (!IsTextExtension(facts.Extension())) {
                        return 10;
                    }

                    if (!IsBetaBaseName(facts.BaseName())) {
                        return 11;
                    }

                    if (!IsAlphaDirectory(facts.DirectoryName())) {
                        return 12;
                    }

                    if (facts.PathLength() != 14 || facts.ExtensionLength() != 4 || facts.BaseNameLength() != 4 || facts.DirectoryNameLength() != 5) {
                        return 13;
                    }

                    if (!MemoryOk(System.IO.Path.TryJoin(joined, "alpha/", "/beta.txt"))) {
                        return 6;
                    }

                    if (!IsJoinedPath(joined.View())) {
                        return 7;
                    }

                    if (!IsOwnedJoinedPath(System.IO.Path.Join("alpha", "beta.txt"))) {
                        return 8;
                    }

                    if (!IsOwnedJoinedPath(System.IO.Path.Join("alpha/", "/beta.txt"))) {
                        return 9;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = appDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
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
    public async Task PackagedStdLibWindowsUnicodePathsCurrentDirectoryAndOwnedUnicodeWritesWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || !OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-windows-unicode-path-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        var workingDirectory = Path.Combine(appDirectory, "unicode-\u03B1-\u65E5");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(workingDirectory);

        var libraryPath = Path.Combine(packageDirectory, "System.lib");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, "app.exe");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-O0", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                import System.Text
                module App

                fn bool StatusOk(System.IO.IOStatus status) {
                    switch (status) {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn bool BoolOrFalse(System.IO.IOResult<bool> result) {
                    switch (result) {
                        case System.IO.IOResult<bool>.Ok(var value):
                            return value;
                        case System.IO.IOResult<bool>.Err(var error):
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

                fn System.IO.File.File OpenOrEmpty(System.IO.IOResult<System.IO.File.File> result) {
                    switch (result) {
                        case System.IO.IOResult<System.IO.File.File>.Ok(var value):
                            return value;
                        case System.IO.IOResult<System.IO.File.File>.Err(var error):
                            return new();
                    }
                }

                export unsafe fn i32[min max] main() {
                    stack mut System.Text.OwnedAscii cwd = new();
                    stack mut i8[min max][12] ownedNameBytes = { 111, 119, 110, 101, 100, 45, -50, -79, 46, 116, 120, 116 };
                    stack mut Ascii ownedName = new Ascii() {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    stack mut i8[min max][14] renamedNameBytes = { 114, 101, 110, 97, 109, 101, 100, 45, -50, -78, 46, 116, 120, 116 };
                    stack mut Ascii renamedName = new Ascii() {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    stack mut i8[min max][13] deleteNameBytes = { 100, 101, 108, 101, 116, 101, 45, -50, -77, 46, 116, 120, 116 };
                    stack mut Ascii deleteName = new Ascii() {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };

                    if (!MemoryOk(System.IO.Path.CurrentDirectory(cwd))) {
                        return 1;
                    }

                    stack mut System.IO.File.File cwdFile = OpenOrEmpty(System.IO.File.Open("cwd.txt", System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line));
                    cwdFile.WriteLine(cwd.View());
                    if (!StatusOk(cwdFile.Close())) {
                        return 3;
                    }

                    stack mut System.IO.File.File file = OpenOrEmpty(System.IO.File.Open(System.Text.AsciiView(ownedName), System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line));
                    file.WriteLine((unicode)"Owned");
                    if (!StatusOk(file.Close())) {
                        return 4;
                    }

                    ownedName = new Ascii() {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    if (!BoolOrFalse(System.IO.File.Exists(System.Text.AsciiView(ownedName)))) {
                        return 5;
                    }

                    ownedName = new Ascii() {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    renamedName = new Ascii() {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    if (!StatusOk(System.IO.File.Move(System.Text.AsciiView(ownedName), System.Text.AsciiView(renamedName)))) {
                        return 6;
                    }

                    renamedName = new Ascii() {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    if (!BoolOrFalse(System.IO.File.Exists(System.Text.AsciiView(renamedName)))) {
                        return 7;
                    }

                    stack mut System.IO.File.File deleteFile = OpenOrEmpty(System.IO.File.Open(System.Text.AsciiView(deleteName), System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line));
                    deleteFile.WriteLine("Delete");
                    if (!StatusOk(deleteFile.Close())) {
                        return 9;
                    }

                    deleteName = new Ascii() {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };
                    if (!StatusOk(System.IO.File.Delete(System.Text.AsciiView(deleteName)))) {
                        return 10;
                    }

                    deleteName = new Ascii() {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };
                    if (BoolOrFalse(System.IO.File.Exists(System.Text.AsciiView(deleteName)))) {
                        return 11;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-O0", "-I", packageDirectory, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = outputPath,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            var processStdout = await process!.StandardOutput.ReadToEndAsync();
            var processStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, processStdout);
            Assert.Equal(string.Empty, processStderr);
            Assert.Equal(
                workingDirectory + "\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "cwd.txt"), System.Text.Encoding.UTF8));
            Assert.Equal(
                "Owned\n",
                await File.ReadAllTextAsync(Path.Combine(workingDirectory, "renamed-\u03B2.txt"), System.Text.Encoding.UTF8));
            Assert.False(File.Exists(Path.Combine(workingDirectory, "owned-\u03B1.txt")));
            Assert.False(File.Exists(Path.Combine(workingDirectory, "delete-\u03B3.txt")));
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
    public void StagedWindowsStdLibPathHelpersUseWindowsSeparatorsAndNormalizationRules()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-windows-path-");

        try
        {
            var stagedSourceRoot = CreateWindowsStagedStdLibSourceRoot(repositoryRoot, tempDirectory.FullName);
            var pathModulePath = Path.Combine(stagedSourceRoot, "System", "IO", "Path.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    File.ReadAllText(pathModulePath),
                    pathModulePath),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                    ModuleResolver: new FileSystemModuleResolver(stagedSourceRoot)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

            Assert.Contains("c\"\\5C\\00\"", llvm, StringComparison.Ordinal);
            Assert.Contains("c\"/\\00\"", llvm, StringComparison.Ordinal);
            Assert.Contains("c\";\\00\"", llvm, StringComparison.Ordinal);

            var isDirectorySeparatorBody = ExtractDefinedFunctionText(
                llvm,
                "define fastcc noundef i1 @IsDirectorySeparatorUnit(",
                "Expected staged Windows path build to emit the path separator unit helper.");
            Assert.Contains("icmp eq i8", isDirectorySeparatorBody, StringComparison.Ordinal);
            Assert.True(
                isDirectorySeparatorBody.Contains("@DirectorySeparatorUnit(", StringComparison.Ordinal)
                || isDirectorySeparatorBody.Contains("@.str.0", StringComparison.Ordinal),
                isDirectorySeparatorBody);
            Assert.Contains("@.str.1", isDirectorySeparatorBody, StringComparison.Ordinal);

            var tryJoinBody = ExtractDefinedFunctionText(
                llvm,
                "define fastcc noundef %System_Memory_MemoryStatus @TryJoin(",
                "Expected TryJoin definition in staged Windows path module.");
            Assert.Contains("icmp eq i8", tryJoinBody, StringComparison.Ordinal);
            Assert.DoesNotContain("@System_Runtime_Platform_Windows_IsDirectorySeparator(", tryJoinBody, StringComparison.Ordinal);
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

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
