using Stark.Compiler;

namespace compiler.StandardLibraryTests;

/// <summary>
/// Do not add files heres unless thye concern multiple pieces of the standard library simultaneously
/// The normal Approach should be to create separate files per module so this file stays relatively lean
/// </summary>

public class StandardLibraryTestSuite
{
    public void StdLibSourceGraphIncludesMilestone7ModuleLayout()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(systemPath), systemPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);

        Assert.True(moduleGraph.ContainsLoadedModule("System"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.BitOperations"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Collections"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Console"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.FileSystem"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.IO"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.IO.File"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.IO.Path"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Math"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Memory"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Net"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Net.Tcp"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Process"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Text"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Threading"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Buffer"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Linux"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Windows"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Syscall"));
        Assert.False(moduleGraph.ContainsLoadedModule("System.IO.Stdout"));
        Assert.False(moduleGraph.ContainsLoadedModule("System.IO.Stderr"));
    }

    public void StdLibSourceCommonErrorResultModelUsesCompactEnumLayouts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibCommonErrorResultModel.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn i32[-2147483648 2147483647] ReadIo(System.IO.IOResult<i32[-2147483648 2147483647]> result) {
                    switch (result) {
                        case System.IO.IOResult<i32[-2147483648 2147483647]>.Ok(var value):
                            return value;
                        case System.IO.IOResult<i32[-2147483648 2147483647]>.Err(var error):
                            return 0;
                    }
                }

                fn i32[-2147483648 2147483647] ReadMemory(System.Memory.MemoryResult<i32[-2147483648 2147483647]> result) {
                    switch (result) {
                        case System.Memory.MemoryResult<i32[-2147483648 2147483647]>.Ok(var value):
                            return value;
                        case System.Memory.MemoryResult<i32[-2147483648 2147483647]>.Err(var error):
                            return 0;
                    }
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "enum-layout"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
        Assert.NotNull(enumLayoutModel);

        AssertStdLibCommonErrorResultLayouts(enumLayoutModel.Layouts);
    }

    public async Task PackagedStdLibCommonErrorResultModelWorksWithoutSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-result-model-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var libraryFileName = OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg.json");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-pkg", "--package-library-file", libraryFileName, "-o", manifestPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(exitCode == 0, stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted package image:", stdout.ToString());
            Assert.Contains($"Package library file: {libraryFileName}", stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.True(File.Exists(manifestPath));

            var appSource =
                """
                import System
                module App

                fn i32[-2147483648 2147483647] ReadIo(System.IO.IOResult<i32[-2147483648 2147483647]> result) {
                    switch (result) {
                        case System.IO.IOResult<i32[-2147483648 2147483647]>.Ok(var value):
                            return value;
                        case System.IO.IOResult<i32[-2147483648 2147483647]>.Err(var error):
                            return 0;
                    }
                }

                fn bool IsIoOk(System.IO.IOStatus status) {
                    switch (status) {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[-2147483648 2147483647] ReadMemory(System.Memory.MemoryResult<i32[-2147483648 2147483647]> result) {
                    switch (result) {
                        case System.Memory.MemoryResult<i32[-2147483648 2147483647]>.Ok(var value):
                            return value;
                        case System.Memory.MemoryResult<i32[-2147483648 2147483647]>.Err(var error):
                            return 0;
                    }
                }

                fn bool IsMemoryOk(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn i32[-2147483648 2147483647] Run() {
                    stack System.IO.IOResult<i32[-2147483648 2147483647]> ioResult =
                        System.IO.IOResult<i32[-2147483648 2147483647]>.Ok(7);
                    stack System.Memory.MemoryResult<i32[-2147483648 2147483647]> memoryResult =
                        System.Memory.MemoryResult<i32[-2147483648 2147483647]>.Ok(11);

                    if (!IsIoOk(System.IO.IOStatus.Ok)) {
                        return 1;
                    }

                    if (!IsMemoryOk(System.Memory.MemoryStatus.Ok)) {
                        return 2;
                    }

                    return ReadIo(ioResult) + ReadMemory(memoryResult);
                }
                """;
            await File.WriteAllTextAsync(appPath, appSource);

            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(appSource, appPath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(packageDirectory),
                    StopAfterPassId: "enum-layout"));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);

            AssertStdLibCommonErrorResultLayouts(enumLayoutModel.Layouts);
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

    public async Task StdLibPackageBuildsFromRepositorySources()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-build-");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var manifestBaseName = OperatingSystem.IsWindows()
            && string.Equals(Path.GetFileName(libraryPath), "System.lib", StringComparison.OrdinalIgnoreCase)
                ? "libSystem"
                : Path.GetFileNameWithoutExtension(libraryPath);
        var manifestPath = Path.Combine(tempDirectory.FullName, manifestBaseName + ".starkpkg.json");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(libraryPath));
            Assert.True(File.Exists(manifestPath));

            var manifest = StarkPackageManifest.FromJson(await File.ReadAllTextAsync(manifestPath));
            Assert.NotNull(manifest);
            var modules = manifest.Modules;

            Assert.Contains(modules, module => module.ModuleName == "System");
            Assert.Contains(modules, module => module.ModuleName == "System.BitOperations");
            Assert.Contains(modules, module => module.ModuleName == "System.Console");
            Assert.Contains(modules, module => module.ModuleName == "System.FileSystem");
            Assert.Contains(modules, module => module.ModuleName == "System.IO");
            Assert.Contains(modules, module => module.ModuleName == "System.IO.File");
            Assert.Contains(modules, module => module.ModuleName == "System.IO.Path");
            Assert.Contains(modules, module => module.ModuleName == "System.Math");
            Assert.Contains(modules, module => module.ModuleName == "System.Memory");
            Assert.Contains(modules, module => module.ModuleName == "System.Net");
            Assert.Contains(modules, module => module.ModuleName == "System.Net.Tcp");
            Assert.Contains(modules, module => module.ModuleName == "System.Process");
            Assert.Contains(modules, module => module.ModuleName == "System.Runtime.Buffer");
            Assert.Contains(modules, module => module.ModuleName == "System.Runtime.Platform");
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains(modules, module => module.ModuleName == "System.Runtime.Platform.Windows");
                Assert.DoesNotContain(modules, module => module.ModuleName == "System.Runtime.Platform.Linux");
            }
            else
            {
                Assert.Contains(modules, module => module.ModuleName == "System.Runtime.Platform.Linux");
                Assert.DoesNotContain(modules, module => module.ModuleName == "System.Runtime.Platform.Windows");
            }
            Assert.Contains(modules, module => module.ModuleName == "System.Syscall");
            Assert.Contains(modules, module => module.ModuleName == "System.Text");
            Assert.Contains(modules, module => module.ModuleName == "System.Threading");
            Assert.DoesNotContain(modules, static module => module.ModuleName.StartsWith("System.Experimental", StringComparison.Ordinal));

            var rootModule = modules.Single(module => module.ModuleName == "System");
            var reExports = rootModule.EffectiveSourceSurface.ReExports?.Select(static item => item.ModuleName).ToArray() ?? [];
            Assert.Contains("System.BitOperations", reExports);
            Assert.Contains("System.Collections", reExports);
            Assert.Contains("System.Console", reExports);
            Assert.Contains("System.FileSystem", reExports);
            Assert.Contains("System.IO", reExports);
            Assert.Contains("System.IO.File", reExports);
            Assert.Contains("System.IO.Path", reExports);
            Assert.Contains("System.Math", reExports);
            Assert.Contains("System.Memory", reExports);
            Assert.Contains("System.Net", reExports);
            Assert.Contains("System.Net.Tcp", reExports);
            Assert.Contains("System.Process", reExports);
            Assert.Contains("System.Text", reExports);
            Assert.Contains("System.Threading", reExports);
            Assert.DoesNotContain(reExports, static moduleName => moduleName.StartsWith("System.Experimental", StringComparison.Ordinal));

            var fileSystemModule = modules.Single(module => module.ModuleName == "System.FileSystem");
            var fileSystemTypes = fileSystemModule.EffectiveSourceSurface.Types?.Select(static item => item.Name).ToArray() ?? [];
            var fileSystemFunctions = fileSystemModule.EffectiveSourceSurface.Functions?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("Directory", fileSystemTypes);
            Assert.Contains("DirectoryReadResult", fileSystemTypes);
            Assert.Contains("FileSystemEntry", fileSystemTypes);
            Assert.Contains("FileSystemEntryKind", fileSystemTypes);
            Assert.Contains("CreateDirectory", fileSystemFunctions);
            Assert.Contains("DeleteDirectory", fileSystemFunctions);
            Assert.Contains("OpenDirectory", fileSystemFunctions);
            Assert.Contains("Exists", fileSystemFunctions);
            Assert.Contains("IsFile", fileSystemFunctions);
            Assert.Contains("IsDirectory", fileSystemFunctions);

            var threadingModule = modules.Single(module => module.ModuleName == "System.Threading");
            var threadingTypes = threadingModule.EffectiveSourceSurface.Types?.Select(static item => item.Name).ToArray() ?? [];
            var threadingAliases = threadingModule.EffectiveSourceSurface.TypeAliases?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("Thread", threadingTypes);
            Assert.Contains("ThreadError", threadingTypes);
            Assert.Contains("ThreadStatus", threadingTypes);
            Assert.Contains("ThreadJoinResult", threadingTypes);
            Assert.Contains("ThreadEntry", threadingAliases);

            var ioModule = modules.Single(module => module.ModuleName == "System.IO");
            var ioTypes = ioModule.EffectiveSourceSurface.Types?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("IOError", ioTypes);
            Assert.Contains("IOResult", ioTypes);
            Assert.Contains("IOStatus", ioTypes);

            var fileModule = modules.Single(module => module.ModuleName == "System.IO.File");
            var fileTypes = fileModule.EffectiveSourceSurface.Types?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("FileBuffering", fileTypes);
            Assert.Contains("FileMode", fileTypes);
            Assert.Contains("File", fileTypes);

            Assert.NotNull(fileModule.EffectiveTypedInterface);
            var fileType = fileModule.EffectiveTypedInterface!.Types.Single(type => type.Name == "File");
            Assert.NotNull(fileType.Destructor);
            Assert.True(fileType.Destructor!.IsMutable);
            Assert.Contains("self.CloseRaw();", fileType.Destructor.BodyText, StringComparison.Ordinal);

            var mathModule = modules.Single(module => module.ModuleName == "System.Math");
            var mathTypes = mathModule.EffectiveSourceSurface.Types?.Select(static item => item.Name).ToArray() ?? [];
            var mathFunctions = mathModule.EffectiveSourceSurface.Functions?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("SinCosF32", mathTypes);
            Assert.Contains("SinCosF64", mathTypes);
            Assert.Contains("Sin", mathFunctions);
            Assert.Contains("Cos", mathFunctions);
            Assert.Contains("Atan2", mathFunctions);
            Assert.Contains("Pow", mathFunctions);
            Assert.Contains("Tanh", mathFunctions);
            Assert.Contains("SinCos", mathFunctions);
            Assert.Contains("Sqrt", mathFunctions);
            Assert.Contains("FusedMultiplyAdd", mathFunctions);
            Assert.Contains("ReciprocalEstimate", mathFunctions);
            Assert.Contains("ReciprocalSqrtEstimate", mathFunctions);
            Assert.Contains("Round", mathFunctions);
            Assert.Contains("Min", mathFunctions);
            Assert.Contains("Max", mathFunctions);

            var bitOperationsModule = modules.Single(module => module.ModuleName == "System.BitOperations");
            var bitOperationsFunctions = bitOperationsModule.EffectiveSourceSurface.Functions?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("LeadingZeroCount", bitOperationsFunctions);
            Assert.Contains("TrailingZeroCount", bitOperationsFunctions);
            Assert.Contains("PopCount", bitOperationsFunctions);
            Assert.Contains("RotateLeft", bitOperationsFunctions);
            Assert.Contains("RotateRight", bitOperationsFunctions);

            var memoryModule = modules.Single(module => module.ModuleName == "System.Memory");
            var memoryTypes = memoryModule.EffectiveSourceSurface.Types?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("MemoryError", memoryTypes);
            Assert.Contains("MemoryStatus", memoryTypes);
            Assert.Contains("MemoryResult", memoryTypes);
            Assert.Contains("Allocator", memoryTypes);

            var collectionsModule = modules.Single(module => module.ModuleName == "System.Collections");
            var collectionsTypes = collectionsModule.EffectiveSourceSurface.Types?.Select(static item => item.Name).ToArray() ?? [];
            Assert.Contains("List", collectionsTypes);
            Assert.Contains("Stack", collectionsTypes);
            Assert.Contains("Queue", collectionsTypes);
            Assert.Contains("LinkedList", collectionsTypes);
            Assert.Contains("Dictionary", collectionsTypes);
            Assert.Contains("Equatable", collectionsTypes);
            Assert.Contains("Hashable", collectionsTypes);
            Assert.Contains("DictionaryKey", collectionsTypes);
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

    public void StdLibSourceConsoleSupportsAsciiAndUnicodeOverloads()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibConsoleUnicodeSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn void Use() {
                    System.Console.Write("ascii");
                    System.Console.Write((unicode)"ascii");
                    System.Console.WriteLine("line");
                    System.Console.WriteLine((unicode)"line");
                    System.Console.WriteError("error");
                    System.Console.WriteError((unicode)"error");
                    System.Console.WriteErrorLine("error-line");
                    System.Console.WriteErrorLine((unicode)"error-line");
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
    public void StdLibSourceConsoleSupportsUnicodeInputSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibConsoleInputSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn void Use() {
                    stack mut System.Text.OwnedUnicode line = new System.Text.OwnedUnicode();
                    stack mut System.Text.OwnedAscii asciiLine = new System.Text.OwnedAscii();
                    stack mut System.Text.OwnedUnicode unicodeLine = new System.Text.OwnedUnicode();
                    stack mut System.Text.OwnedUnicode unit = new System.Text.OwnedUnicode();

                    switch (System.Console.ReadLine()) {
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var lineError):
                            return;
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var lineValue):
                            line = lineValue;
                    }

                    switch (System.Console.ReadAsciiLine()) {
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Err(var asciiError):
                            return;
                        case System.Memory.MemoryResult<System.Text.OwnedAscii>.Ok(var asciiValue):
                            asciiLine = asciiValue;
                    }

                    switch (System.Console.ReadUnicodeLine()) {
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var unicodeError):
                            return;
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var unicodeValue):
                            unicodeLine = unicodeValue;
                    }

                    switch (System.Console.Read()) {
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Err(var unitError):
                            return;
                        case System.Memory.MemoryResult<System.Text.OwnedUnicode>.Ok(var unitValue):
                            unit = unitValue;
                    }

                    System.Console.WriteLine(line.View());
                    System.Console.WriteLine(asciiLine.View());
                    System.Console.WriteLine(unicodeLine.View());
                    System.Console.WriteLine(unit.View());
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
    public async Task StdLibSourceUnicodeConsoleInputWorksAtRuntime()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-console-input-source-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack Unicode line = System.Console.ReadLine();
                    stack Ascii asciiLine = System.Console.ReadAsciiLine();
                    stack Unicode unit = System.Console.Read();

                    if (line.Length != 5) {
                        return 1;
                    }

                    if (asciiLine.Length != 5) {
                        return 2;
                    }

                    if (unit.Length != 1) {
                        return 3;
                    }

                    if (line.Data == null || *line.Data != 104) {
                        return 4;
                    }

                    if (*(&line.Data[1]) != 101) {
                        return 5;
                    }

                    if (asciiLine.Data == null || *asciiLine.Data != 98) {
                        return 6;
                    }

                    if (*(&asciiLine.Data[1]) != 121) {
                        return 7;
                    }

                    if (unit.Data == null || *unit.Data != 945) {
                        return 8;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, "hello\nbytes\nÎ±");
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
    public void StdLibSourceTextBuiltinsAndPathHelperSurfaceCompile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPathCallerBufferSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System
                module Demo

                fn bool MemoryOk(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                    }
                }

                unsafe fn i32[-2147483648 2147483647] Use() {
                    stack mut System.Text.OwnedAscii owned = new();
                    stack mut System.Text.OwnedAscii joined = new();

                    stack rawptr<i8[-128 127]> asciiData = System.Text.AsciiData("demo");
                    stack i64[-9223372036854775808 9223372036854775807] asciiLength = System.Text.AsciiLength("demo");
                    stack rawptr<i32[-2147483648 2147483647]> unicodeData = System.Text.UnicodeData((unicode)"demo");
                    stack i64[-9223372036854775808 9223372036854775807] unicodeLength = System.Text.UnicodeLength((unicode)"demo");
                    stack bool status = MemoryOk(System.IO.Path.CurrentDirectory(owned));
                    stack bool joinedOk = MemoryOk(System.IO.Path.TryJoin(joined, "demo", "file.txt"));
                    stack ascii extension = System.IO.Path.Extension("demo/file.txt");
                    stack ascii baseName = System.IO.Path.BaseName("demo/file.txt");
                    stack ascii directory = System.IO.Path.DirectoryName("demo/file.txt");

                    if (asciiData == null || unicodeData == null) {
                        return 1;
                    }

                    if (asciiLength != 4 || unicodeLength != 4) {
                        return 2;
                    }

                    if (!status) {
                        return 3;
                    }

                    if (!joinedOk) {
                        return 4;
                    }

                    if (System.Text.AsciiLength(extension) == 0
                        || System.Text.AsciiLength(baseName) == 0
                        || System.Text.AsciiLength(directory) == 0) {
                        return 4;
                    }

                    return 0;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
    public void StdLibSourceRuntimeBufferModuleSupportsLinearAndRingOperations()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibRuntimeBufferSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Runtime.Buffer
                import System.Memory
                module Demo

                unsafe fn i32[-2147483648 2147483647] Use() {
                    stack mut System.Runtime.Buffer.FixedByteBuffer512 linear = new System.Runtime.Buffer.FixedByteBuffer512();
                    stack rawmutptr<i8[-128 127]> writePtr = linear.WritePointer();
                    if (writePtr == null) {
                        return 1;
                    }

                    *writePtr = (i8[-128 127])65;
                    linear.AdvanceWrite(1);

                    stack rawptr<i8[-128 127]> readPtr = linear.ReadPointer();
                    if (readPtr == null || *readPtr != (i8[-128 127])65) {
                        return 2;
                    }

                    linear.AdvanceRead(1);
                    if (!linear.IsEmpty()) {
                        return 3;
                    }

                    stack mut System.Runtime.Buffer.DynamicByteBuffer buffer = new System.Runtime.Buffer.DynamicByteBuffer();
                    stack mut i8[-128 127] value = 0;
                    switch (buffer.WriteByte((i8[-128 127])66)) {
                        case System.Memory.MemoryStatus.Err(var writeError):
                            return 4;
                        case System.Memory.MemoryStatus.Ok:
                    }

                    if (!buffer.TryReadByte(value) || value != (i8[-128 127])66) {
                        return 5;
                    }

                    return 0;
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }
    public void StdLibSourceCurrentDirectoryUsesSyscallBackedLinuxPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define i64 @LinuxSyscall2PathBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall2PathBuffer(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@getcwd(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@strlen(", llvm, StringComparison.Ordinal);
    }
    public void StdLibSourceConsoleAsciiWritesUseSyscallBackedLinuxPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define fastcc noundef i32 @WriteAsciiToHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call fastcc i32 @WriteAsciiToHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("inttoptr i8 1 to ptr", llvm, StringComparison.Ordinal);
        Assert.Contains("inttoptr i8 2 to ptr", llvm, StringComparison.Ordinal);
    }
    public void StdLibSourceConsoleUnicodeWritesUseSyscallBackedLinuxPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define fastcc noundef i32 @WriteUnicodeToHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fputws(", llvm, StringComparison.Ordinal);
    }
    public void StdLibSourceLinuxFileOperationsUseSyscallBackedPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define fastcc noundef ptr @OpenFileRead(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @CloseFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i64 @WriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4OpenAt(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall1Handle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleBuffer(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3AtPath(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4RenameAt(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fopen(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fclose(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fread(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fwrite(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@remove(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@rename(", llvm, StringComparison.Ordinal);
    }
    public void StdLibSourceLinuxFileExistsUsesStatSyscallPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define i64 @LinuxSyscall4StatAt(", llvm, StringComparison.Ordinal);

        var functionBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @FileExists(",
            "Expected FileExists definition in emitted LLVM.");
        var pathExistsBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @PathExists(",
            "Expected PathExists definition in emitted LLVM.");
        var helperBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryReadPathMode(",
            "Expected TryReadPathMode definition in emitted LLVM.");

        Assert.Contains("@PathExists(", functionBody, StringComparison.Ordinal);
        Assert.Contains("@TryReadPathMode(", pathExistsBody, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall4StatAt(", helperBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@OpenFileRead(", helperBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@CloseFile(", helperBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@stat(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fstatat(", llvm, StringComparison.Ordinal);
    }
    public void StdLibSourceLinuxTerminalDetectionUsesIoctlSyscallPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var linuxPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Linux.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(linuxPath),
                linuxPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define i64 @LinuxSyscall3HandleRequestPointer(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @IsTerminal(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i64 @LinuxSyscall3HandleRequestPointer(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@isatty(", llvm, StringComparison.Ordinal);
    }
    public void StdLibSourceWindowsConsoleAndFileOperationsUseWin32Apis()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var windowsPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Windows.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(windowsPath),
                windowsPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("declare ptr @GetStdHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare ptr @CreateFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @DeleteFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @MoveFileExW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetFileAttributesW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetCurrentDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @GetConsoleMode(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @WriteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("declare i32 @ReadFile(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Platform_Windows_WriteFile__", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@System_Runtime_Platform_Windows_ReadFile__", llvm, StringComparison.Ordinal);

        Assert.Contains("define fastcc noundef i32 @WriteStdoutAscii(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef ptr @OpenFileRead(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i32 @DeleteFile(", llvm, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @FileExists(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @GetStdHandle(", llvm, StringComparison.Ordinal);
        Assert.Contains("call ptr @CreateFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetCurrentDirectoryW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetConsoleMode(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @DeleteFileW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @MoveFileExW(", llvm, StringComparison.Ordinal);
        Assert.Contains("call i32 @GetFileAttributesW(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fopen(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fclose(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fread(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@fwrite(", llvm, StringComparison.Ordinal);
    }
    public void StdLibSourceWindowsWidePathCopiesUseInlineAsmHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var windowsPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform", "Windows.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(windowsPath),
                windowsPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var copyBody = ExtractDefinedFunctionText(
            llvm,
            "define fastcc noundef i1 @TryCopyWideRange(",
            "Expected TryCopyWideRange definition in emitted LLVM.");

        Assert.Contains("define void @CopyWideUnits(", llvm, StringComparison.Ordinal);
        Assert.Contains("rep movsw", llvm, StringComparison.Ordinal);
        Assert.Contains("call void @CopyWideUnits(", copyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@llvm.memcpy", copyBody, StringComparison.Ordinal);
    }
    public void StagedWindowsStdLibBuildRoutesPlatformCallsThroughWindowsModule()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-windows-stage-");

        try
        {
            var stagedSourceRoot = CreateWindowsStagedStdLibSourceRoot(repositoryRoot, tempDirectory.FullName);
            var systemPath = Path.Combine(stagedSourceRoot, "System.stark");
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(
                    File.ReadAllText(systemPath),
                    systemPath),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                    ModuleResolver: new FileSystemModuleResolver(stagedSourceRoot)));

            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
            Assert.NotNull(moduleGraph);
            Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform"));
            Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Windows"));
            Assert.False(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Linux"));

            var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            Assert.Contains("@GetStdHandle(", llvm, StringComparison.Ordinal);
            Assert.Contains("@CreateFileW(", llvm, StringComparison.Ordinal);
            Assert.DoesNotContain("@LinuxSyscall", llvm, StringComparison.Ordinal);
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
    public void SourceStdLibBuildRoutesPlatformCallsThroughLinuxModuleForLinuxTargets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var systemPath = Path.Combine(sourceRoot, "System.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(systemPath),
                systemPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform"));
        Assert.True(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Linux"));
        Assert.False(moduleGraph.ContainsLoadedModule("System.Runtime.Platform.Windows"));

        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("@LinuxSyscall", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@GetStdHandle(", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("@CreateFileW(", llvm, StringComparison.Ordinal);
    }
    public void RootWindowsStdLibCompileKeepsWriteBufferToHandleOnDirectMirPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                File.ReadAllText(systemPath),
                systemPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null),
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);

        var function = Assert.Single(
            mir.Functions,
            static candidate => candidate.Name == "System.Runtime.Platform.Windows.WriteBufferToHandle");
        Assert.True(function.SupportsDirectCodeGeneration);

        Assert.DoesNotContain(
            result.Logs,
            log => log.Kind == CompilerLogKind.Gap
                && string.Equals(log.SymbolName, "System.Runtime.Platform.Windows.WriteBufferToHandle", StringComparison.Ordinal)
                && string.Equals(log.Operation, "EmitAssignmentFromExpression", StringComparison.Ordinal));
    }
    public void SystemSyscallModuleSelectsExpectedLinuxShimPerArchitecture(string targetTriple, string expectedInlineAsm)
    {
        var repositoryRoot = FindRepositoryRoot();
        var syscallPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Syscall.stark");
        var source = File.ReadAllText(syscallPath);
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(source, syscallPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo(targetTriple, null)));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains(expectedInlineAsm, llvm, StringComparison.Ordinal);
    }
    public async Task PackagedStdLibCanBeConsumedWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-app-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

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
                """
                import System
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack mut i8[-128 127][16] asciiBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

                    stack mut Ascii ownedAscii = new Ascii() {
                        Data = &asciiBuffer[0],
                        Length = 0,
                        Capacity = 16
                    };

                    stack Unicode ownedUnicode = new Unicode() {
                        Data = null,
                        Length = 0,
                        Capacity = 4
                    };

                    stack System.Text.Encoding encoding = System.Text.Encoding.UTF8;
                    stack System.IO.IOStatus status = System.IO.IOStatus.Ok;
                    if (ownedAscii.Capacity != 16) {
                        return 1;
                    }

                    if (ownedUnicode.Capacity != 4) {
                        return 2;
                    }

                    if (!System.Text.TryConcatAscii(&ownedAscii, "Stark", " IO")) {
                        return 3;
                    }

                    stack Ascii fileAscii = new Ascii() {
                        Data = ownedAscii.Data,
                        Length = ownedAscii.Length,
                        Capacity = ownedAscii.Capacity
                    };

                    stack Ascii consoleAscii = new Ascii() {
                        Data = ownedAscii.Data,
                        Length = ownedAscii.Length,
                        Capacity = ownedAscii.Capacity
                    };

                    stack rawptr<i8[-128 127]> handle = System.IO.File.OpenWrite("io-test.txt");
                    System.IO.File.WriteLine(handle, System.Text.AsciiView(fileAscii));
                    System.IO.File.Close(handle);

                    System.Console.WriteLine(System.Text.AsciiView(consoleAscii));
                    System.Console.WriteLine(System.IO.Path.DirectorySeparator());

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
            Assert.Equal($"Stark IO\n{(OperatingSystem.IsWindows() ? "\\" : "/")}\n", processStdout);
            Assert.Equal(string.Empty, processStderr);
            Assert.Equal("Stark IO\n", await File.ReadAllTextAsync(Path.Combine(appDirectory, "io-test.txt")));
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
    public async Task PackagedStdLibMathIntrinsicsWorkWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !SupportsSystemMathHardwareAsmTarget(targetInfo.Triple))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-math-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

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
                """
                import System
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack f64 zero = 0.0;
                    stack f64 one = 1.0;
                    stack f64 two = 2.0;
                    stack f64 three = 3.0;
                    stack f64 eight = 8.0;
                    stack i32[-2147483648 2147483647] oneI32 = 1;
                    stack i32[-2147483648 2147483647] twoI32 = 2;
                    stack i32[-2147483648 2147483647] threeI32 = 3;
                    stack i32[-2147483648 2147483647] fourI32 = 4;
                    stack i32[-2147483648 2147483647] thirtyOne = 31;
                    stack i32[-2147483648 2147483647] minI32 = -2147483647 - 1;
                    stack i64[-9223372036854775808 9223372036854775807] oneI64 = 1;
                    stack i64[-9223372036854775808 9223372036854775807] twoI64 = 2;
                    stack i64[-9223372036854775808 9223372036854775807] threeI64 = 3;
                    stack i64[-9223372036854775808 9223372036854775807] fourI64 = 4;
                    stack i64[-9223372036854775808 9223372036854775807] sixtyThree = 63;

                    if (System.Math.Sin(zero) != zero) {
                        return 1;
                    }

                    if (System.Math.Cos(zero) != one) {
                        return 2;
                    }

                    if (System.Math.Tan(zero) != zero) {
                        return 3;
                    }

                    if (System.Math.Exp(zero) != one) {
                        return 4;
                    }

                    if (System.Math.Exp2(three) != eight) {
                        return 5;
                    }

                    if (System.Math.Log(one) != zero) {
                        return 6;
                    }

                    if (System.Math.Log2(eight) != three) {
                        return 7;
                    }

                    if (System.Math.Log10(one) != zero) {
                        return 8;
                    }

                    if (System.Math.Asin(zero) != zero) {
                        return 9;
                    }

                    if (System.Math.Acos(one) != zero) {
                        return 10;
                    }

                    if (System.Math.Atan(zero) != zero) {
                        return 11;
                    }

                    if (System.Math.Atan2(zero, one) != zero) {
                        return 12;
                    }

                    if (System.Math.Pow(two, three) != eight) {
                        return 13;
                    }

                    if (System.Math.Sinh(zero) != zero) {
                        return 14;
                    }

                    if (System.Math.Cosh(zero) != one) {
                        return 15;
                    }

                    if (System.Math.Tanh(zero) != zero) {
                        return 16;
                    }

                    stack System.Math.SinCosF64 pair = System.Math.SinCos(zero);
                    if (pair.Sin != zero || pair.Cos != one) {
                        return 17;
                    }

                    if (System.Math.Sqrt(9.0) != three) {
                        return 18;
                    }

                    stack f32 reciprocal = System.Math.ReciprocalEstimate(4.0);
                    if (reciprocal < 0.24 || reciprocal > 0.26) {
                        return 19;
                    }

                    stack f32 reciprocalSqrt = System.Math.ReciprocalSqrtEstimate(4.0);
                    if (reciprocalSqrt < 0.49 || reciprocalSqrt > 0.51) {
                        return 20;
                    }

                    if (System.Math.Ceiling(2.25) != three) {
                        return 21;
                    }

                    if (System.Math.Floor(2.75) != two) {
                        return 22;
                    }

                    if (System.Math.Truncate(-2.75) != -2.0) {
                        return 23;
                    }

                    if (System.Math.Round(2.5) != two) {
                        return 24;
                    }

                    if (System.Math.Round(3.5) != 4.0) {
                        return 25;
                    }

                    if (System.Math.Min(two, three) != two) {
                        return 26;
                    }

                    if (System.Math.Max(two, three) != three) {
                        return 27;
                    }

                    if (System.BitOperations.LeadingZeroCount(oneI32) != thirtyOne) {
                        return 28;
                    }

                    if (System.BitOperations.TrailingZeroCount(fourI32) != twoI32) {
                        return 29;
                    }

                    if (System.BitOperations.PopCount(threeI32) != twoI32) {
                        return 30;
                    }

                    if (System.BitOperations.RotateLeft(oneI32, thirtyOne) != minI32) {
                        return 31;
                    }

                    if (System.BitOperations.RotateRight(twoI32, oneI32) != oneI32) {
                        return 32;
                    }

                    if (System.BitOperations.LeadingZeroCount(oneI64) != sixtyThree) {
                        return 33;
                    }

                    if (System.BitOperations.TrailingZeroCount(fourI64) != twoI64) {
                        return 34;
                    }

                    if (System.BitOperations.PopCount(threeI64) != twoI64) {
                        return 35;
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
    public async Task PackagedStdLibFusedMultiplyAddWorksWithoutSourceWhenRuntimeSupportsIt()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !SupportsSystemMathHardwareAsmTarget(targetInfo.Triple)
            || !SupportsSystemMathFusedMultiplyAddRuntime(targetInfo.Triple))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-fma-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

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
                """
                import System
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack f64 two = 2.0;
                    stack f64 three = 3.0;
                    stack f64 four = 4.0;
                    stack f64 value = System.Math.FusedMultiplyAdd(two, three, four);
                    if (value != 10.0) {
                        return 1;
                    }

                    stack f32 twoSmall = 2.0;
                    stack f32 threeSmall = 3.0;
                    stack f32 fourSmall = 4.0;
                    stack f32 small = System.Math.FusedMultiplyAdd(twoSmall, threeSmall, fourSmall);
                    if (small != 10.0) {
                        return 2;
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
    public async Task PackagedStdLibConsoleReturnsIoStatusWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-console-status-");
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
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                fn bool IsOk(System.IO.IOStatus status) {
                    switch (status) {
                        case System.IO.IOStatus.Ok:
                            return true;
                        case System.IO.IOStatus.Err(var error):
                            return false;
                    }
                }

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    if (!IsOk(System.Console.Write("Console"))) {
                        return 1;
                    }

                    if (!IsOk(System.Console.WriteLine(" Status"))) {
                        return 2;
                    }

                    if (!IsOk(System.Console.WriteErrorLine("stderr works"))) {
                        return 3;
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
            Assert.Equal("Console Status\n", processStdout);
            Assert.Equal("stderr works\n", processStderr);
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
    public async Task PackagedStdLibUnicodeConsoleInputWorksWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-console-input-");
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
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(
                appPath,
                """
                import System
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack Unicode line = System.Console.ReadLine();
                    stack Ascii asciiLine = System.Console.ReadAsciiLine();
                    stack Unicode unit = System.Console.Read();

                    if (line.Length != 5) {
                        return 1;
                    }

                    if (asciiLine.Length != 5) {
                        return 2;
                    }

                    if (unit.Length != 1) {
                        return 3;
                    }

                    if (line.Data == null || *line.Data != 104) {
                        return 4;
                    }

                    if (*(&line.Data[1]) != 101) {
                        return 5;
                    }

                    if (asciiLine.Data == null || *asciiLine.Data != 98) {
                        return 6;
                    }

                    if (*(&asciiLine.Data[1]) != 121) {
                        return 7;
                    }

                    if (unit.Data == null || *unit.Data != 945) {
                        return 8;
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

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, appDirectory, "hello\nbytes\nÎ±");
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
    public async Task SourceRuntimePlatformTerminalDetectionSeesRedirectedStdoutAsNonTerminal()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-runtime-terminal-detect-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, "app");

        try
        {
            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Platform
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    if (System.Runtime.Platform.IsTerminal((rawptr<i8[-128 127]>)1)) {
                        return 1;
                    }

                    if (System.Runtime.Platform.IsTerminal((rawptr<i8[-128 127]>)2)) {
                        return 2;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath],
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
                WorkingDirectory = tempDirectory.FullName,
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
    public async Task PackagedStdLibLinuxArchiveHasNoLibcSymbolReferences()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || !OperatingSystem.IsLinux())
        {
            return;
        }

        var nmPath = FindFirstAvailableTool("nm", "llvm-nm");
        if (nmPath is null)
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-nm-");
        var libraryPath = Path.Combine(tempDirectory.FullName, "libSystem.a");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(libraryPath));

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = nmPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add("-A");
            startInfo.ArgumentList.Add(libraryPath);

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);

            var nmStdout = await process.StandardOutput.ReadToEndAsync();
            var nmStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            var actionableNmStderr = string.Join(
                Environment.NewLine,
                nmStderr
                    .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
                    .Where(static line => !line.Contains("no symbols", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(string.Empty, actionableNmStderr);

            Assert.DoesNotContain(" U fopen", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fclose", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fflush", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fread", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fwrite", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fputs", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U fputws", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U getcwd", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U remove", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U rename", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U strlen", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U malloc", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U realloc", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U free", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U pthread_create", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U pthread_join", nmStdout, StringComparison.Ordinal);
            Assert.DoesNotContain(" U pthread_detach", nmStdout, StringComparison.Ordinal);
            AssertNoExplicitCAllocatorSymbols(ExtractUndefinedSymbols(nmStdout));
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
    public async Task PackagedStdLibWindowsArchiveHasNoCrtSymbolReferences()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _)
            || !OperatingSystem.IsWindows())
        {
            return;
        }

        var nmPath = FindFirstAvailableTool("llvm-nm", "nm");
        if (nmPath is null)
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-windows-nm-");
        var libraryPath = Path.Combine(tempDirectory.FullName, "System.lib");

        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("Emitted static library:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(libraryPath));

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = nmPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add("-A");
            startInfo.ArgumentList.Add(libraryPath);

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);

            var nmStdout = await process!.StandardOutput.ReadToEndAsync();
            var nmStderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.True(
                IsNoSymbolsOnlyDiagnostic(nmStderr),
                "Expected llvm-nm/nm to emit no diagnostics except benign 'no symbols' messages for stripped archive members."
                + Environment.NewLine
                + nmStderr);

            var undefinedSymbols = ExtractUndefinedSymbols(nmStdout);
            Assert.Contains("CreateFileW", undefinedSymbols);
            Assert.Contains("ReadFile", undefinedSymbols);
            Assert.Contains("WriteFile", undefinedSymbols);
            Assert.Contains("DeleteFileW", undefinedSymbols);
            Assert.Contains("MoveFileExW", undefinedSymbols);
            Assert.Contains("GetCurrentDirectoryW", undefinedSymbols);
            Assert.Contains("GetConsoleMode", undefinedSymbols);

            Assert.DoesNotContain("fopen", undefinedSymbols);
            Assert.DoesNotContain("fclose", undefinedSymbols);
            Assert.DoesNotContain("fflush", undefinedSymbols);
            Assert.DoesNotContain("fread", undefinedSymbols);
            Assert.DoesNotContain("fwrite", undefinedSymbols);
            Assert.DoesNotContain("fputs", undefinedSymbols);
            Assert.DoesNotContain("fputws", undefinedSymbols);
            Assert.DoesNotContain("_wfopen", undefinedSymbols);
            Assert.DoesNotContain("_wrename", undefinedSymbols);
            Assert.DoesNotContain("_wremove", undefinedSymbols);
            Assert.DoesNotContain("_wgetcwd", undefinedSymbols);
            Assert.DoesNotContain("_getcwd", undefinedSymbols);
            AssertNoExplicitCAllocatorSymbols(undefinedSymbols);
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
    public async Task PackagedStdLibSyscallModuleCanBeConsumedWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo)
            || !OperatingSystem.IsLinux()
            || !targetInfo.Triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-syscall-");
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
                """
                import System.Syscall
                module App

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    if (System.Syscall.Syscall0(39) <= 0) {
                        return 1;
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
    public async Task SourceRuntimeBufferModuleCanExecuteLinearAndRingOperations()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var bufferPath = Path.Combine(repositoryRoot, "stdlib", "src", "System", "Runtime", "Buffer.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-runtime-buffer-");
        var runtimeDirectory = Path.Combine(tempDirectory.FullName, "System", "Runtime");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
        Directory.CreateDirectory(runtimeDirectory);

        try
        {
            File.Copy(bufferPath, Path.Combine(runtimeDirectory, "Buffer.stark"));

            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Runtime.Buffer
                import System.Memory
                module App

                fn bool IsOk(System.Memory.MemoryStatus status) {
                    switch (status) {
                        case System.Memory.MemoryStatus.Err(var error):
                            return false;
                        case System.Memory.MemoryStatus.Ok:
                            return true;
                    }
                }

                export unsafe ffi fn i32[-2147483648 2147483647] main() {
                    stack mut System.Runtime.Buffer.FixedByteBuffer512 linear = new System.Runtime.Buffer.FixedByteBuffer512();
                    if (linear.Capacity() != 512 || linear.Readable() != 0 || linear.Writable() != 512) {
                        return 1;
                    }

                    stack rawmutptr<i8[-128 127]> writePtr = linear.WritePointer();
                    if (writePtr == null) {
                        return 2;
                    }

                    *writePtr = (i8[-128 127])65;
                    linear.AdvanceWrite(1);

                    stack rawptr<i8[-128 127]> readPtr = linear.ReadPointer();
                    if (readPtr == null || *readPtr != (i8[-128 127])65) {
                        return 3;
                    }

                    linear.AdvanceRead(1);
                    if (!linear.IsEmpty()) {
                        return 4;
                    }

                    stack mut System.Runtime.Buffer.DynamicByteBuffer buffer = new System.Runtime.Buffer.DynamicByteBuffer();
                    stack mut i8[-128 127] value = 0;

                    if (!IsOk(buffer.WriteByte((i8[-128 127])66)) || !IsOk(buffer.WriteByte((i8[-128 127])67))) {
                        return 5;
                    }

                    if (!buffer.TryReadByte(value) || value != (i8[-128 127])66) {
                        return 6;
                    }

                    if (!buffer.TryReadByte(value) || value != (i8[-128 127])67) {
                        return 7;
                    }

                    for willexit (stack mut i64[-9223372036854775808 9223372036854775807] i = 0; i < 1024; i += 1) {
                        if (!IsOk(buffer.WriteByte((i8[-128 127])90))) {
                            return 8;
                        }

                        if (!buffer.TryReadByte(value) || value != (i8[-128 127])90) {
                            return 9;
                        }
                    }

                    if (!buffer.IsEmpty()) {
                        return 10;
                    }

                    return 0;
                }
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", Path.Combine(repositoryRoot, "stdlib", "src"), "-o", outputPath],
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
                WorkingDirectory = tempDirectory.FullName,
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

    protected static async Task WriteAllocatorAuditAppAsync(string appPath)
    {
        await File.WriteAllTextAsync(
            appPath,
            """
            import System
            module App

            struct Box {
                i32[min max] Value;
            }

            export unsafe ffi fn i32[min max] main() {
                stack mut i32[min max] checksum = 0;

                for willexit (stack mut i32[0 128] i = 0; i < 128; i += 1) {
                    heap Box box = new Box() {
                        Value = (i32[min max])i
                    };
                    checksum = checksum + box.Value;
                }

                if (checksum != 8128) {
                    return 1;
                }

                return 0;
            }
            """);
    }

    protected static async Task AssertBinaryHasNoExplicitCAllocatorSymbolReferencesAsync(string nmPath, string binaryPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = nmPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(binaryPath);

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = await process!.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.True(
            IsNoSymbolsOnlyDiagnostic(stderr),
            "Expected llvm-nm/nm to emit no diagnostics except the benign 'no symbols' message for stripped binaries."
            + Environment.NewLine
            + stderr);
        AssertNoExplicitCAllocatorSymbols(ExtractUndefinedSymbols(stdout));
    }

    private static bool IsNoSymbolsOnlyDiagnostic(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return true;
        }

        return stderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(static line => line.EndsWith(": no symbols", StringComparison.Ordinal));
    }

    private static void AssertNoExplicitCAllocatorSymbols(IReadOnlySet<string> undefinedSymbols)
    {
        foreach (var symbol in new[]
                 {
                     "malloc",
                     "_malloc",
                     "realloc",
                     "_realloc",
                     "free",
                     "_free",
                     "calloc",
                     "_calloc",
                     "aligned_alloc",
                     "_aligned_malloc",
                     "posix_memalign"
                 })
        {
            Assert.DoesNotContain(symbol, undefinedSymbols);
        }
    }

    private static void AssertStdLibCommonErrorResultLayouts(IReadOnlyDictionary<string, EnumLayoutSymbol> layouts)
    {
        var ioError = layouts["System.IO.IOError"];
        AssertCompactTag(ioError, bitWidth: 8, maxTagValue: 6);
        Assert.Equal(["$tag", "$Unknown_0"], ioError.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("i32", ioError.OrderedFields[1].Type.DisplayName);

        var ioStatus = layouts["System.IO.IOStatus"];
        AssertCompactTag(ioStatus, bitWidth: 8, maxTagValue: 1);
        Assert.Equal(["$tag", "$Err_0"], ioStatus.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("System.IO.IOError", ioStatus.OrderedFields[1].Type.DisplayName);

        var ioResult = Assert.Single(
            layouts.Values,
            static layout =>
                layout.EnumName.StartsWith("System.IO.IOResult<", StringComparison.Ordinal)
                && layout.OrderedFields.Count >= 2
                && layout.OrderedFields[1].Type.DisplayName == "i32");
        AssertCompactTag(ioResult, bitWidth: 8, maxTagValue: 1);
        Assert.Equal(["$tag", "$Ok_0", "$Err_0"], ioResult.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("i32", ioResult.OrderedFields[1].Type.DisplayName);
        Assert.Equal("System.IO.IOError", ioResult.OrderedFields[2].Type.DisplayName);

        var memoryError = layouts["System.Memory.MemoryError"];
        AssertCompactTag(memoryError, bitWidth: 8, maxTagValue: 3);
        Assert.Equal(["$tag"], memoryError.OrderedFields.Select(static field => field.Name).ToArray());

        var memoryStatus = layouts["System.Memory.MemoryStatus"];
        AssertCompactTag(memoryStatus, bitWidth: 8, maxTagValue: 1);
        Assert.Equal(["$tag", "$Err_0"], memoryStatus.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("System.Memory.MemoryError", memoryStatus.OrderedFields[1].Type.DisplayName);

        var memoryResult = Assert.Single(
            layouts.Values,
            static layout =>
                layout.EnumName.StartsWith("System.Memory.MemoryResult<", StringComparison.Ordinal)
                && layout.OrderedFields.Count >= 2
                && layout.OrderedFields[1].Type.DisplayName == "i32");
        AssertCompactTag(memoryResult, bitWidth: 8, maxTagValue: 1);
        Assert.Equal(["$tag", "$Ok_0", "$Err_0"], memoryResult.OrderedFields.Select(static field => field.Name).ToArray());
        Assert.Equal("i32", memoryResult.OrderedFields[1].Type.DisplayName);
        Assert.Equal("System.Memory.MemoryError", memoryResult.OrderedFields[2].Type.DisplayName);
    }

    private static void AssertCompactTag(EnumLayoutSymbol layout, int bitWidth, int maxTagValue)
    {
        Assert.Equal("$tag", layout.TagField.Name);
        Assert.Equal(StarkTypeKind.Integer, layout.TagField.Type.Kind);
        Assert.Equal(bitWidth, layout.TagField.Type.BitWidth);
        Assert.Equal(System.Numerics.BigInteger.Zero, layout.TagField.Type.RangeMin);
        Assert.Equal(new System.Numerics.BigInteger(maxTagValue), layout.TagField.Type.RangeMax);
    }

    protected static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root for stdlib integration tests.");
    }

    protected static string? FindFirstAvailableTool(params string[] toolNames)
    {
        var directories = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                directories.Add(Path.Combine(programFiles, "LLVM", "bin"));
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                directories.Add(Path.Combine(programFilesX86, "LLVM", "bin"));
            }
        }

        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!seenDirectories.Add(directory))
            {
                continue;
            }

            foreach (var toolName in toolNames)
            {
                foreach (var candidateName in GetToolCandidateNames(toolName))
                {
                    var candidate = Path.Combine(directory, candidateName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetToolCandidateNames(string toolName)
    {
        yield return toolName;

        if (OperatingSystem.IsWindows() && !Path.HasExtension(toolName))
        {
            yield return toolName + ".exe";
        }
    }

    private static HashSet<string> ExtractUndefinedSymbols(string nmOutput)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in nmOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var marker = line.LastIndexOf(" U ", StringComparison.Ordinal);
            if (marker < 0 || marker + 3 >= line.Length)
            {
                continue;
            }

            var symbol = line[(marker + 3)..].Trim();
            if (symbol.StartsWith("__imp_", StringComparison.Ordinal))
            {
                symbol = symbol["__imp_".Length..];
            }

            var versionMarker = symbol.IndexOf('@', StringComparison.Ordinal);
            if (versionMarker > 0)
            {
                symbol = symbol[..versionMarker];
            }

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                symbols.Add(symbol);
            }
        }

        return symbols;
    }

    protected static string ExtractDefinedFunctionText(string llvm, string signaturePrefix, string missingMessage)
    {
        var functionStart = llvm.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(functionStart >= 0, missingMessage);

        var bodyStart = llvm.IndexOf('{', functionStart);
        Assert.True(bodyStart > functionStart, $"Expected '{signaturePrefix}' to include a function body.");

        var depth = 0;
        for (var index = bodyStart; index < llvm.Length; index++)
        {
            var current = llvm[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return llvm.Substring(functionStart, index - functionStart + 1);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected '{signaturePrefix}' body to terminate in emitted LLVM.");
    }

    protected static string CreateWindowsStagedStdLibSourceRoot(string repositoryRoot, string stagingDirectory)
    {
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var stagedSourceRoot = Path.Combine(stagingDirectory, "src");
        CopyDirectory(sourceRoot, stagedSourceRoot);

        var dispatchTemplatePath = Path.Combine(repositoryRoot, "stdlib", "templates", "System.Runtime.Platform.WindowsDispatch.stark");
        var stagedPlatformPath = Path.Combine(stagedSourceRoot, "System", "Runtime", "Platform.stark");
        File.Copy(dispatchTemplatePath, stagedPlatformPath, overwrite: true);
        return stagedSourceRoot;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.GetFiles(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, destinationPath, overwrite: true);
        }

        foreach (var childDirectory in Directory.GetDirectories(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(childDirectory));
            CopyDirectory(childDirectory, destinationPath);
        }
    }

    protected static void AssertCompilerLogsEmitted(string text)
    {
        Assert.Equal(string.Empty, text);
    }

    protected static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessWithUtf8StdinAsync(
        string fileName,
        string workingDirectory,
        string stdinText)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        var stdinBytes = System.Text.Encoding.UTF8.GetBytes(stdinText);
        await process!.StandardInput.BaseStream.WriteAsync(stdinBytes);
        await process.StandardInput.BaseStream.FlushAsync();
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static bool SupportsSystemMathHardwareAsmTarget(string triple)
    {
        var dashIndex = triple.IndexOf('-');
        var architecture = dashIndex >= 0
            ? triple[..dashIndex]
            : triple;

        return architecture is
            "x86_64"
            or "amd64"
            or "aarch64"
            or "arm64"
            or "x86"
            or "i386"
            or "i486"
            or "i586"
            or "i686";
    }

    private static bool SupportsSystemMathFusedMultiplyAddRuntime(string triple)
    {
        var dashIndex = triple.IndexOf('-');
        var architecture = dashIndex >= 0
            ? triple[..dashIndex]
            : triple;

        return architecture switch
        {
            "x86_64" or "amd64" or "x86" or "i386" or "i486" or "i586" or "i686"
                => System.Runtime.Intrinsics.X86.Fma.IsSupported,
            "aarch64" or "arm64"
                => true,
            _ => false
        };
    }

    public static IEnumerable<object[]> SystemSyscallArchitectureCases()
    {
        yield return
        [
            "x86_64-unknown-linux-gnu",
            "call i64 asm sideeffect \"syscall\", \"={rax},0,{rdi},{rsi},{rdx},{r10},{r8},{r9},~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}\""
        ];
        yield return
        [
            "aarch64-unknown-linux-gnu",
            "call i64 asm sideeffect \"svc #0\", \"={x0},{x8},0,{x1},{x2},{x3},{x4},{x5},~{memory}\""
        ];
        yield return
        [
            "riscv64-unknown-linux-gnu",
            "call i64 asm sideeffect \"ecall\", \"={a0},{a7},0,{a1},{a2},{a3},{a4},{a5},~{memory}\""
        ];
        yield return
        [
            "i386-unknown-linux-gnu",
            "call i64 asm sideeffect \"int $$0x80\", \"={eax},0,{ebx},{ecx},{edx},{esi},{edi},{ebp},~{memory},~{dirflag},~{fpsr},~{flags}\""
        ];
        yield return
        [
            "arm-unknown-linux-gnueabihf",
            "call i64 asm sideeffect \"svc #0\", \"={r0},{r7},0,{r1},{r2},{r3},{r4},{r5},~{memory}\""
        ];
    }
}

