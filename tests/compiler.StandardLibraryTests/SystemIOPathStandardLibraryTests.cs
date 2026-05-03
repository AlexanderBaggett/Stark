using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOPathStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceExperimentalPathLowersThroughDynamicStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalPathLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Experimental.IO.Path
                import System.Experimental.Text
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

                fn i64[0 max] BuildAndInspect() {
                    stack mut System.Experimental.Text.OwnedAscii path = new();
                    if (!Ok(System.Experimental.IO.Path.TryJoin(path, "alpha/", "/beta.txt"))) {
                        return 0;
                    }

                    stack ascii view = path.View();
                    stack System.Experimental.IO.Path.PathFacts facts = System.Experimental.IO.Path.GetFacts(view);
                    return (i64[0 max])(path.Length()
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
    public void StdLibSourceExperimentalPathTryJoinUsesTailRegionPointerCopies()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Experimental", "IO", "Path.stark");
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
            "Expected experimental TryJoin definition in path module.");
        var tryJoinPointerRangesBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryJoinPointerRanges(",
            "Expected experimental TryJoinPointerRanges definition in path module.");
        var tryJoinConstBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryJoinConst(",
            "Expected experimental TryJoinConst definition in path module.");
        var tryNormalizeBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryNormalizeSeparators(",
            "Expected experimental TryNormalizeSeparators definition in path module.");
        var tryNormalizeConstBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef %System_Memory_MemoryStatus @TryNormalizeSeparatorsConst(",
            "Expected experimental TryNormalizeSeparatorsConst definition in path module.");
        var getConstFactsBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc void @GetConstFacts(",
            "Expected experimental GetConstFacts definition in path module.");

        Assert.Contains("@TryJoinPointerRanges", tryJoinBody, StringComparison.Ordinal);
        Assert.Contains("@TryJoinPointerRanges", tryJoinConstBody, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(tryJoinPointerRangesBody, "@System_Experimental_Memory_InitializeBytesFromPointerDisjoint") >= 2,
            "Expected TryJoin pointer core to copy left and right path ranges through explicit tail-region pointer initialization helpers.");
        Assert.DoesNotContain("@System_Experimental_Text_OwnedAscii_AppendAscii", tryJoinConstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Experimental_Text_OwnedAscii_AppendAscii", tryNormalizeConstBody, StringComparison.Ordinal);
        Assert.Contains("@AppendNormalizedSeparatorsCore", tryNormalizeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@AppendNormalizedSeparators(", tryNormalizeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", tryJoinConstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("slot_snapshot", tryNormalizeConstBody, StringComparison.Ordinal);
        Assert.Contains("@System_Experimental_Text_AsciiLength", getConstFactsBody, StringComparison.Ordinal);
        Assert.Contains("@System_Experimental_Text_AsciiData", getConstFactsBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceExperimentalPathCorrectnessSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalPathCorrectness.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Experimental.IO.Path
                import System.Experimental.Text
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

                fn bool IsOwnedJoinedPath(System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> result) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == 14 && IsJoinedPath(value.View());
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                fn bool IsOwnedNormalizedPath(System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> result) {
                    switch (result) {
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var value):
                            return value.Length() == 20 && IsNormalizedPath(value.View());
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var error):
                            return false;
                    }
                }

                fn bool Probe() {
                    stack mut System.Experimental.Text.OwnedAscii joined = new();
                    if (!Ok(System.Experimental.IO.Path.TryJoin(joined, "alpha/", "/beta.txt"))) {
                        return false;
                    }

                    stack ascii joinedView = joined.View();
                    if (!IsJoinedPath(joinedView)) {
                        return false;
                    }

                    stack System.Experimental.IO.Path.PathFacts facts = System.Experimental.IO.Path.GetFacts(joinedView);
                    if (!IsTextExtension(facts.Extension()) || !IsBetaBaseName(facts.BaseName()) || !IsAlphaDirectory(facts.DirectoryName())) {
                        return false;
                    }

                    if (facts.PathLength() != 14 || facts.ExtensionLength() != 4 || facts.BaseNameLength() != 4 || facts.DirectoryNameLength() != 5) {
                        return false;
                    }

                    stack mut System.Experimental.Text.OwnedAscii normalized = new();
                    if (!Ok(System.Experimental.IO.Path.TryNormalizeSeparators(normalized, "alpha//beta///gamma.txt"))) {
                        return false;
                    }

                    stack System.Experimental.IO.Path.PathFacts constFacts = System.Experimental.IO.Path.GetConstFacts("alpha/beta.txt");
                    if (!IsTextExtension(constFacts.Extension())
                        || !IsBetaBaseName(System.Experimental.IO.Path.BaseNameConst("alpha/beta.txt"))
                        || !IsAlphaDirectory(System.Experimental.IO.Path.DirectoryNameConst("alpha/beta.txt"))) {
                        return false;
                    }

                    stack mut System.Experimental.Text.OwnedAscii constJoined = new();
                    if (!Ok(System.Experimental.IO.Path.TryJoinConst(constJoined, ConstLeft, ConstRight)) || !IsJoinedPath(constJoined.View())) {
                        return false;
                    }

                    stack mut System.Experimental.Text.OwnedAscii constNormalized = new();
                    if (!Ok(System.Experimental.IO.Path.TryNormalizeSeparatorsConst(constNormalized, ConstNormalizedSource)) || !IsNormalizedPath(constNormalized.View())) {
                        return false;
                    }

                    return IsNormalizedPath(normalized.View())
                        && IsOwnedJoinedPath(System.Experimental.IO.Path.Join("alpha", "beta.txt"))
                        && IsOwnedJoinedPath(System.Experimental.IO.Path.JoinConst(ConstLeft, ConstRight))
                        && IsOwnedJoinedPath(System.Experimental.IO.Path.Join("alpha/", "/beta.txt"))
                        && IsOwnedNormalizedPath(System.Experimental.IO.Path.NormalizeSeparators("alpha//beta///gamma.txt"))
                        && IsOwnedNormalizedPath(System.Experimental.IO.Path.NormalizeSeparatorsConst(ConstNormalizedSource));
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
    public async Task SourceStdLibExperimentalPathCorrectnessExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-path-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Experimental.IO.Path
                import System.Experimental.Text
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

                finite law i8[-128 127] UnitAt(ascii value, i64[0 max] index) {
                    stack rawptr<i8[-128 127]> data = System.Experimental.Text.AsciiData(value);
                    return *(&data[index]);
                }

                finite law i8[-128 127] SeparatorUnit() {
                    return UnitAt(System.Experimental.IO.Path.DirectorySeparator(), 0);
                }

                finite law bool IsSeparatorUnit(i8[-128 127] value) {
                    if (value == SeparatorUnit()) {
                        return true;
                    }

                    stack ascii alternate = System.Experimental.IO.Path.AlternateDirectorySeparator();
                    if (System.Experimental.Text.AsciiLength(alternate) <= 0) {
                        return false;
                    }

                    return value == UnitAt(alternate, 0);
                }

                fn bool IsJoinedPath(ascii value) {
                    if (System.Experimental.Text.AsciiLength(value) != 14) {
                        return false;
                    }

                    return UnitAt(value, 0) == (i8[-128 127])97
                        && UnitAt(value, 4) == (i8[-128 127])97
                        && IsSeparatorUnit(UnitAt(value, 5))
                        && UnitAt(value, 6) == (i8[-128 127])98
                        && UnitAt(value, 10) == (i8[-128 127])46
                        && UnitAt(value, 13) == (i8[-128 127])116;
                }

                fn bool IsNormalizedPath(ascii value) {
                    if (System.Experimental.Text.AsciiLength(value) != 20) {
                        return false;
                    }

                    return UnitAt(value, 5) == SeparatorUnit()
                        && UnitAt(value, 10) == SeparatorUnit()
                        && UnitAt(value, 11) == (i8[-128 127])103
                        && UnitAt(value, 16) == (i8[-128 127])46
                        && UnitAt(value, 19) == (i8[-128 127])116;
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
                    return System.Experimental.Text.AsciiLength(value) == 0;
                }

                fn bool CheckFacts() {
                    stack System.Experimental.IO.Path.PathFacts facts = System.Experimental.IO.Path.GetFacts("alpha/beta.txt");
                    if (facts.PathLength() != 14
                        || facts.ExtensionLength() != 4
                        || facts.BaseNameLength() != 4
                        || facts.DirectoryNameLength() != 5
                        || !IsTextExtension(facts.Extension())
                        || !IsBetaBaseName(facts.BaseName())
                        || !IsAlphaDirectory(facts.DirectoryName())) {
                        return false;
                    }

                    stack System.Experimental.IO.Path.PathFacts constFacts = System.Experimental.IO.Path.GetConstFacts("alpha/beta.txt");
                    if (constFacts.PathLength() != 14
                        || constFacts.ExtensionLength() != 4
                        || constFacts.BaseNameLength() != 4
                        || constFacts.DirectoryNameLength() != 5
                        || !IsTextExtension(System.Experimental.IO.Path.ExtensionConst("alpha/beta.txt"))
                        || !IsBetaBaseName(System.Experimental.IO.Path.BaseNameConst("alpha/beta.txt"))
                        || !IsAlphaDirectory(System.Experimental.IO.Path.DirectoryNameConst("alpha/beta.txt"))) {
                        return false;
                    }

                    stack System.Experimental.IO.Path.PathFacts archive = System.Experimental.IO.Path.GetFacts("archive.tar.gz");
                    if (!IsGzExtension(archive.Extension()) || !IsArchiveBaseName(archive.BaseName()) || archive.DirectoryNameLength() != 0) {
                        return false;
                    }

                    stack System.Experimental.IO.Path.PathFacts hidden = System.Experimental.IO.Path.GetFacts("alpha/.hidden");
                    if (!IsEmpty(hidden.Extension()) || !IsHiddenBaseName(hidden.BaseName()) || !IsAlphaDirectory(hidden.DirectoryName())) {
                        return false;
                    }

                    stack System.Experimental.IO.Path.PathFacts root = System.Experimental.IO.Path.GetFacts("/");
                    return root.PathLength() == 1
                        && root.DirectoryNameLength() == 1
                        && root.BaseNameLength() == 0
                        && IsEmpty(System.Experimental.IO.Path.GetFacts("").BaseName())
                        && IsEmpty(System.Experimental.IO.Path.Extension(".gitignore"))
                        && IsHiddenBaseName(System.Experimental.IO.Path.BaseName("alpha/.hidden"));
                }

                fn i32[min max] CheckJoinAndNormalize() {
                    stack mut System.Experimental.Text.OwnedAscii joined = new();
                    if (!Ok(System.Experimental.IO.Path.TryJoin(joined, "alpha/", "/beta.txt"))) {
                        return 1;
                    }

                    if (System.Experimental.Text.AsciiLength(joined.View()) != 14) {
                        return 7;
                    }

                    if (!IsSeparatorUnit(UnitAt(joined.View(), 5))) {
                        return 8;
                    }

                    if (!IsJoinedPath(joined.View())) {
                        return 9;
                    }

                    stack mut System.Experimental.Text.OwnedAscii normalized = new();
                    if (!Ok(System.Experimental.IO.Path.TryNormalizeSeparators(normalized, "alpha//beta///gamma.txt")) || !IsNormalizedPath(normalized.View())) {
                        return 2;
                    }

                    stack mut System.Experimental.Text.OwnedAscii constJoined = new();
                    if (!Ok(System.Experimental.IO.Path.TryJoinConst(constJoined, ConstLeft, ConstRight)) || !IsJoinedPath(constJoined.View())) {
                        return 10;
                    }

                    stack mut System.Experimental.Text.OwnedAscii constNormalized = new();
                    if (!Ok(System.Experimental.IO.Path.TryNormalizeSeparatorsConst(constNormalized, ConstNormalizedSource)) || !IsNormalizedPath(constNormalized.View())) {
                        return 11;
                    }

                    stack System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> ownedJoin = System.Experimental.IO.Path.Join("alpha", "beta.txt");
                    switch (ownedJoin) {
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var ownedJoinValue):
                            if (!IsJoinedPath(ownedJoinValue.View())) {
                                return 3;
                            }
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var ownedJoinError):
                            return 4;
                    }

                    stack System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> ownedConstJoin = System.Experimental.IO.Path.JoinConst(ConstLeft, ConstRight);
                    switch (ownedConstJoin) {
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var ownedConstJoinValue):
                            if (!IsJoinedPath(ownedConstJoinValue.View())) {
                                return 12;
                            }
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var ownedConstJoinError):
                            return 13;
                    }

                    stack System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> ownedNormalize = System.Experimental.IO.Path.NormalizeSeparators("alpha//beta///gamma.txt");
                    switch (ownedNormalize) {
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var ownedNormalizeValue):
                            if (!IsNormalizedPath(ownedNormalizeValue.View())) {
                                return 5;
                            }
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var ownedNormalizeError):
                            return 6;
                    }

                    stack System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii> ownedConstNormalize = System.Experimental.IO.Path.NormalizeSeparatorsConst(ConstNormalizedSource);
                    switch (ownedConstNormalize) {
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Ok(var ownedConstNormalizeValue):
                            if (!IsNormalizedPath(ownedConstNormalizeValue.View())) {
                                return 14;
                            }
                        case System.Memory.MemoryResult<System.Experimental.Text.OwnedAscii>.Err(var ownedConstNormalizeError):
                            return 15;
                    }

                    return 0;
                }

                fn bool CheckSelfViewAliases() {
                    stack mut System.Experimental.Text.OwnedAscii selfJoin = new();
                    if (!Ok(selfJoin.AppendAscii("alpha"))) {
                        return false;
                    }

                    stack ascii sameView = selfJoin.View();
                    if (!Ok(System.Experimental.IO.Path.TryJoin(selfJoin, sameView, sameView))) {
                        return false;
                    }

                    if (System.Experimental.Text.AsciiLength(selfJoin.View()) != 11
                        || UnitAt(selfJoin.View(), 5) != SeparatorUnit()
                        || UnitAt(selfJoin.View(), 6) != (i8[-128 127])97) {
                        return false;
                    }

                    stack mut System.Experimental.Text.OwnedAscii rightAlias = new();
                    if (!Ok(rightAlias.AppendAscii("beta.txt"))) {
                        return false;
                    }

                    stack ascii rightView = rightAlias.View();
                    if (!Ok(System.Experimental.IO.Path.TryJoin(rightAlias, "alpha", rightView)) || !IsJoinedPath(rightAlias.View())) {
                        return false;
                    }

                    stack mut System.Experimental.Text.OwnedAscii normalizeAlias = new();
                    if (!Ok(normalizeAlias.AppendAscii("alpha//beta///gamma.txt"))) {
                        return false;
                    }

                    stack ascii normalizeView = normalizeAlias.View();
                    return Ok(System.Experimental.IO.Path.TryNormalizeSeparators(normalizeAlias, normalizeView))
                        && IsNormalizedPath(normalizeAlias.View());
                }

                fn i32[min max] CheckTooLargeNormalization() {
                    stack mut i8[-128 127][1] storage = { 47 };
                    stack Ascii huge = new Ascii() {
                        Data = &storage[0],
                        Length = (i64[min max])((2**63) - 1),
                        Capacity = (i64[min max])((2**63) - 1)
                    };
                    stack mut System.Experimental.Text.OwnedAscii destination = new();
                    stack ascii hugeView = System.Experimental.Text.AsciiView(huge);
                    if (System.Experimental.Text.AsciiLength(hugeView) == 0) {
                        return 1;
                    }

                    stack System.Memory.MemoryStatus status = System.Experimental.IO.Path.TryNormalizeSeparators(destination, hugeView);
                    if (Ok(status)) {
                        return 2;
                    }

                    if (destination.Length() != 0) {
                        return 3;
                    }

                    return 0;
                }

                export ffi fn i32[min max] main() {
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
        var zeroBytes = string.Join(", ", Enumerable.Repeat("0", 256));

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
                module App

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack mut i8[-128 127][256] buffer = { {{zeroBytes}} };
                    stack mut Ascii owned = new Ascii() {
                        Data = &buffer[0],
                        Length = 0,
                        Capacity = 256
                    };

                    if (!System.IO.Path.CurrentDirectory(&owned)) {
                        return 1;
                    }

                    if (owned.Length <= 0) {
                        return 2;
                    }

                    stack System.IO.IOStatus status = System.Console.WriteLine(System.Text.AsciiView(owned));
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

            Assert.Equal(0, exitCode);
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
        var zeroBytes = string.Join(", ", Enumerable.Repeat("0", 64));

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
                module App

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

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack mut i8[-128 127][64] buffer = { {{zeroBytes}} };
                    stack mut Ascii joined = new Ascii() {
                        Data = &buffer[0],
                        Length = 0,
                        Capacity = 64
                    };

                    if (!System.IO.Path.TryJoin(&joined, "alpha", "beta.txt")) {
                        return 1;
                    }

                    stack Ascii joinedPath = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsJoinedPath(System.Text.AsciiView(joinedPath))) {
                        return 2;
                    }

                    stack Ascii joinedExtension = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsTextExtension(System.IO.Path.Extension(System.Text.AsciiView(joinedExtension)))) {
                        return 3;
                    }

                    stack Ascii joinedBaseName = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsBetaBaseName(System.IO.Path.BaseName(System.Text.AsciiView(joinedBaseName)))) {
                        return 4;
                    }

                    stack Ascii joinedDirectory = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsAlphaDirectory(System.IO.Path.DirectoryName(System.Text.AsciiView(joinedDirectory)))) {
                        return 5;
                    }

                    stack Ascii joinedFacts = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    stack System.IO.Path.PathFacts facts = System.IO.Path.GetFacts(System.Text.AsciiView(joinedFacts));
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

                    if (!System.IO.Path.TryJoin(&joined, "alpha/", "/beta.txt")) {
                        return 6;
                    }

                    stack Ascii joinedNormalized = new Ascii() {
                        Data = joined.Data,
                        Length = joined.Length,
                        Capacity = joined.Capacity
                    };
                    if (!IsJoinedPath(System.Text.AsciiView(joinedNormalized))) {
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

            Assert.Equal(0, exitCode);
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
        var currentDirectoryZeros = string.Join(", ", Enumerable.Repeat("0", 512));

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                $$"""
                import System
                module App

                export ffi fn i32[-2147483648 2147483647] main() {
                    stack mut i8[-128 127][512] cwdStorage = { {{currentDirectoryZeros}} };
                    stack mut Ascii cwd = new Ascii() {
                        Data = &cwdStorage[0],
                        Length = 0,
                        Capacity = 512
                    };
                    stack mut i8[-128 127][12] ownedNameBytes = { 111, 119, 110, 101, 100, 45, -50, -79, 46, 116, 120, 116 };
                    stack mut Ascii ownedName = new Ascii() {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    stack mut i8[-128 127][14] renamedNameBytes = { 114, 101, 110, 97, 109, 101, 100, 45, -50, -78, 46, 116, 120, 116 };
                    stack mut Ascii renamedName = new Ascii() {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    stack mut i8[-128 127][13] deleteNameBytes = { 100, 101, 108, 101, 116, 101, 45, -50, -77, 46, 116, 120, 116 };
                    stack mut Ascii deleteName = new Ascii() {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };

                    if (!System.IO.Path.CurrentDirectory(&cwd)) {
                        return 1;
                    }

                    stack rawptr<i8[-128 127]> cwdHandle = System.IO.File.OpenWrite("cwd.txt");
                    if (cwdHandle == null) {
                        return 2;
                    }

                    System.IO.File.WriteLine(cwdHandle, System.Text.AsciiView(cwd));
                    if (System.IO.File.Close(cwdHandle) != 0) {
                        return 3;
                    }

                    stack mut System.IO.File.File file = System.IO.File.Open(System.Text.AsciiView(ownedName), System.IO.File.FileMode.Write, System.IO.File.FileBuffering.Line);
                    file.WriteLine((unicode)"Owned");
                    if (file.Close() != 0) {
                        return 4;
                    }

                    ownedName = new Ascii() {
                        Data = &ownedNameBytes[0],
                        Length = 12,
                        Capacity = 12
                    };
                    if (!System.IO.File.Exists(System.Text.AsciiView(ownedName))) {
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
                    if (System.IO.File.Move(System.Text.AsciiView(ownedName), System.Text.AsciiView(renamedName)) != 0) {
                        return 6;
                    }

                    renamedName = new Ascii() {
                        Data = &renamedNameBytes[0],
                        Length = 14,
                        Capacity = 14
                    };
                    if (!System.IO.File.Exists(System.Text.AsciiView(renamedName))) {
                        return 7;
                    }

                    stack rawptr<i8[-128 127]> deleteHandle = System.IO.File.OpenWrite(System.Text.AsciiView(deleteName));
                    if (deleteHandle == null) {
                        return 8;
                    }

                    System.IO.File.WriteLine(deleteHandle, "Delete");
                    if (System.IO.File.Close(deleteHandle) != 0) {
                        return 9;
                    }

                    deleteName = new Ascii() {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };
                    if (System.IO.File.Delete(System.Text.AsciiView(deleteName)) != 0) {
                        return 10;
                    }

                    deleteName = new Ascii() {
                        Data = &deleteNameBytes[0],
                        Length = 13,
                        Capacity = 13
                    };
                    if (System.IO.File.Exists(System.Text.AsciiView(deleteName))) {
                        return 11;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
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
                "define internal dso_local fastcc noundef i1 @__stark_law_clone_System_Runtime_Platform_Windows_IsDirectorySeparator(",
                "Expected staged Windows path build to emit the Windows separator law clone.");
            Assert.Contains("icmp eq i8", isDirectorySeparatorBody, StringComparison.Ordinal);
            Assert.Contains(", 47", isDirectorySeparatorBody, StringComparison.Ordinal);
            Assert.Contains(", 92", isDirectorySeparatorBody, StringComparison.Ordinal);

            var tryJoinBody = ExtractDefinedFunctionText(
                llvm,
                "define fastcc noundef i1 @TryJoin(",
                "Expected TryJoin definition in staged Windows path module.");
            Assert.Contains("call fastcc i1 @IsDirectorySeparatorUnit(", tryJoinBody, StringComparison.Ordinal);
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
