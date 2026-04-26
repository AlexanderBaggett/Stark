using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOPathStandardLibraryTests : StandardLibraryTestSuite
{
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
}
