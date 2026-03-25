namespace Stark.Compiler;

internal static class CompilerCli
{
    private const string Usage = "Usage: compiler [path-to-stark-file] [--check|--emit-mir|--emit-ssa|--emit-llvm|--emit-obj|--emit-lib|--emit-exe] [-I dir|--search-dir dir]* [-o output]";

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var mode = CliMode.Default;
        string? inputPath = null;
        string? outputPath = null;
        var searchDirectories = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (TryParseMode(argument, out var parsedMode))
            {
                if (mode != CliMode.Default)
                {
                    await stderr.WriteLineAsync("Choose only one output mode.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                mode = parsedMode;
                continue;
            }

            if (string.Equals(argument, "-o", StringComparison.Ordinal))
            {
                if (outputPath is not null || index + 1 >= args.Length)
                {
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                outputPath = args[++index];
                continue;
            }

            if (string.Equals(argument, "-I", StringComparison.Ordinal)
                || string.Equals(argument, "--search-dir", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length)
                {
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                searchDirectories.Add(args[++index]);
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                await stderr.WriteLineAsync($"Unknown option '{argument}'.");
                await stderr.WriteLineAsync(Usage);
                return 1;
            }

            if (inputPath is not null)
            {
                await stderr.WriteLineAsync(Usage);
                return 1;
            }

            inputPath = argument;
        }

        var source = inputPath is not null
            ? await File.ReadAllTextAsync(inputPath)
            : await stdin.ReadToEndAsync();

        var moduleResolver = ResolveModuleResolver(inputPath, searchDirectories);
        var pipeline = DefaultCompilerPipeline.Create();
        var requiresTargetInfo = mode is CliMode.EmitLlvmIr or CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable;
        var targetInfo = requiresTargetInfo
            && NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo)
                ? detectedTargetInfo
                : null;
        var compilerOptions = new CompilerOptions(
            EmitLlvmIr: mode is CliMode.EmitLlvmIr or CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable,
            TargetInfo: targetInfo,
            StopAfterPassId: ResolveStopAfterPassId(mode),
            ModuleResolver: moduleResolver,
            QualifyModuleSymbols: mode == CliMode.EmitLibrary);
        var result = pipeline.Run(
            new CompilationInput(source, inputPath),
            compilerOptions);

        if (!result.Succeeded)
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                await stderr.WriteLineAsync(diagnostic.ToString());
            }

            return 1;
        }

        switch (mode)
        {
            case CliMode.Check:
                await stdout.WriteLineAsync("Check succeeded.");
                return 0;
            case CliMode.EmitMir:
                return await EmitTextArtifactAsync(outputPath, stdout, result, CompilerArtifactKeys.MidLevelIr, ArtifactTextRenderer.Render);
            case CliMode.EmitSsa:
                return await EmitTextArtifactAsync(outputPath, stdout, result, CompilerArtifactKeys.SsaIr, ArtifactTextRenderer.Render);
            case CliMode.EmitLlvmIr:
                return await EmitTextArtifactAsync(outputPath, stdout, result, CompilerArtifactKeys.LlvmIrModule, static module => module.Text);
            case CliMode.EmitObject:
                return await EmitObjectAsync(outputPath, inputPath, stdout, stderr, result);
            case CliMode.EmitLibrary:
                return await EmitLibraryAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions);
            case CliMode.EmitExecutable:
                return await EmitExecutableAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions);
            default:
                var executedPasses = result.Executions.Count(static execution => execution.Status == PassExecutionStatus.Executed);
                await stdout.WriteLineAsync($"Compilation pipeline succeeded. Executed {executedPasses} passes.");
                return 0;
        }
    }

    private static async Task<int> EmitExecutableAsync(
        string? outputPath,
        string? inputPath,
        TextWriter stdout,
        TextWriter stderr,
        CompilationResult result,
        CompilerOptions compilerOptions)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            await stderr.WriteLineAsync("LLVM IR was not produced.");
            return 1;
        }

        var resolvedOutputPath = outputPath ?? DeriveExecutableOutputPath(inputPath, result);
        var linkInputs = new List<string>();
        var linkedLibraries = new HashSet<string>(StringComparer.Ordinal);
        var tempDirectory = Directory.CreateTempSubdirectory("stark-link-");

        try
        {
            var rootObjectPath = Path.Combine(tempDirectory.FullName, $"root{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
            var rootObjectResult = NativeToolchain.EmitObject(llvmModule.Text, rootObjectPath);
            if (!rootObjectResult.Succeeded)
            {
                await WriteToolchainFailureAsync(stdout, stderr, rootObjectResult);
                return 1;
            }

            linkInputs.Add(rootObjectResult.OutputPath);

            if (result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
                && loadedModules is not null)
            {
                foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
                {
                    if (!string.IsNullOrWhiteSpace(module.Reference.LibraryPath))
                    {
                        var libraryPath = Path.GetFullPath(module.Reference.LibraryPath!);
                        if (linkedLibraries.Add(libraryPath))
                        {
                            linkInputs.Add(libraryPath);
                        }

                        continue;
                    }

                    var dependencyResult = CompileDependencyObject(module, compilerOptions, tempDirectory.FullName);
                    if (!dependencyResult.Success)
                    {
                        foreach (var diagnostic in dependencyResult.Diagnostics)
                        {
                            await stderr.WriteLineAsync(diagnostic.ToString());
                        }

                        if (dependencyResult.ToolchainResult is not null)
                        {
                            await WriteToolchainFailureAsync(stdout, stderr, dependencyResult.ToolchainResult);
                        }

                        return 1;
                    }

                    if (dependencyResult.ObjectPath is not null)
                    {
                        linkInputs.Add(dependencyResult.ObjectPath);
                    }
                }
            }

            var toolchainResult = NativeToolchain.LinkExecutable(linkInputs, resolvedOutputPath);
            if (!toolchainResult.Succeeded)
            {
                await WriteToolchainFailureAsync(stdout, stderr, toolchainResult);
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(toolchainResult.StandardOutput))
            {
                await stdout.WriteAsync(toolchainResult.StandardOutput);
            }

            if (!string.IsNullOrWhiteSpace(toolchainResult.StandardError))
            {
                await stderr.WriteAsync(toolchainResult.StandardError);
            }

            await stdout.WriteLineAsync($"Emitted executable: {toolchainResult.OutputPath}");
            return 0;
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

    private static async Task<int> EmitLibraryAsync(
        string? outputPath,
        string? inputPath,
        TextWriter stdout,
        TextWriter stderr,
        CompilationResult result,
        CompilerOptions compilerOptions)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            await stderr.WriteLineAsync("LLVM IR was not produced.");
            return 1;
        }

        var resolvedOutputPath = outputPath ?? DeriveLibraryOutputPath(inputPath, result);
        var objectPaths = new List<string>();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-lib-");

        try
        {
            var rootObjectPath = Path.Combine(tempDirectory.FullName, $"root{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
            var rootObjectResult = NativeToolchain.EmitObject(llvmModule.Text, rootObjectPath);
            if (!rootObjectResult.Succeeded)
            {
                await WriteToolchainFailureAsync(stdout, stderr, rootObjectResult);
                return 1;
            }

            objectPaths.Add(rootObjectResult.OutputPath);

            if (result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
                && loadedModules is not null)
            {
                foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
                {
                    var dependencyResult = CompileDependencyObject(module, compilerOptions, tempDirectory.FullName);
                    if (!dependencyResult.Success)
                    {
                        foreach (var diagnostic in dependencyResult.Diagnostics)
                        {
                            await stderr.WriteLineAsync(diagnostic.ToString());
                        }

                        if (dependencyResult.ToolchainResult is not null)
                        {
                            await WriteToolchainFailureAsync(stdout, stderr, dependencyResult.ToolchainResult);
                        }

                        return 1;
                    }

                    if (dependencyResult.ObjectPath is not null)
                    {
                        objectPaths.Add(dependencyResult.ObjectPath);
                    }
                }
            }

            var toolchainResult = NativeToolchain.CreateStaticLibrary(objectPaths, resolvedOutputPath);
            if (!toolchainResult.Succeeded)
            {
                await WriteToolchainFailureAsync(stdout, stderr, toolchainResult);
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(toolchainResult.StandardOutput))
            {
                await stdout.WriteAsync(toolchainResult.StandardOutput);
            }

            if (!string.IsNullOrWhiteSpace(toolchainResult.StandardError))
            {
                await stderr.WriteAsync(toolchainResult.StandardError);
            }

            var manifestPath = DeriveLibraryManifestPath(toolchainResult.OutputPath);
            var manifest = PackageManifestBuilder.Create(result, toolchainResult.OutputPath);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJson());

            await stdout.WriteLineAsync($"Emitted static library: {toolchainResult.OutputPath}");
            await stdout.WriteLineAsync($"Emitted package manifest: {manifestPath}");
            return 0;
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

    private static async Task<int> EmitObjectAsync(
        string? outputPath,
        string? inputPath,
        TextWriter stdout,
        TextWriter stderr,
        CompilationResult result)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            await stderr.WriteLineAsync("LLVM IR was not produced.");
            return 1;
        }

        var resolvedOutputPath = outputPath ?? DeriveObjectOutputPath(inputPath, result);
        var toolchainResult = NativeToolchain.EmitObject(llvmModule.Text, resolvedOutputPath);
        if (!toolchainResult.Succeeded)
        {
            if (!string.IsNullOrWhiteSpace(toolchainResult.StandardOutput))
            {
                await stderr.WriteAsync(toolchainResult.StandardOutput);
            }

            if (!string.IsNullOrWhiteSpace(toolchainResult.StandardError))
            {
                await stderr.WriteAsync(toolchainResult.StandardError);
            }

            return 1;
        }

        if (!string.IsNullOrWhiteSpace(toolchainResult.StandardOutput))
        {
            await stdout.WriteAsync(toolchainResult.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(toolchainResult.StandardError))
        {
            await stderr.WriteAsync(toolchainResult.StandardError);
        }

        await stdout.WriteLineAsync($"Emitted object file: {toolchainResult.OutputPath}");
        return 0;
    }

    private static IModuleResolver? ResolveModuleResolver(string? inputPath, IReadOnlyList<string> searchDirectories)
    {
        var resolvedDirectories = new List<string>();

        if (inputPath is not null)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var directory = Path.GetDirectoryName(fullInputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                resolvedDirectories.Add(directory);
            }
        }

        resolvedDirectories.AddRange(searchDirectories.Where(static path => !string.IsNullOrWhiteSpace(path)));

        var environmentSearchPath = Environment.GetEnvironmentVariable("STARK_PATH");
        if (!string.IsNullOrWhiteSpace(environmentSearchPath))
        {
            foreach (var path in environmentSearchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                resolvedDirectories.Add(path);
            }
        }

        return resolvedDirectories.Count == 0
            ? null
            : new FileSystemModuleResolver(resolvedDirectories);
    }

    private static async Task WriteToolchainFailureAsync(TextWriter stdout, TextWriter stderr, NativeToolchainResult toolchainResult)
    {
        if (!string.IsNullOrWhiteSpace(toolchainResult.StandardOutput))
        {
            await stderr.WriteAsync(toolchainResult.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(toolchainResult.StandardError))
        {
            await stderr.WriteAsync(toolchainResult.StandardError);
        }
    }

    private static DependencyCompileResult CompileDependencyObject(
        LoadedModuleDocument module,
        CompilerOptions rootOptions,
        string tempDirectory)
    {
        var dependencyPipeline = DefaultCompilerPipeline.Create();
        var dependencyResult = dependencyPipeline.Run(
            new CompilationInput(
                module.Reference.FilePath is not null ? File.ReadAllText(module.Reference.FilePath) : string.Empty,
                module.Reference.FilePath),
            rootOptions with
            {
                EmitLlvmIr = true,
                StopAfterPassId = null,
                QualifyModuleSymbols = true
            });

        if (!dependencyResult.Succeeded)
        {
            return new DependencyCompileResult(false, null, dependencyResult.Diagnostics, null);
        }

        if (!dependencyResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            return new DependencyCompileResult(false, null, [], null);
        }

        var objectPath = Path.Combine(
            tempDirectory,
            $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
        var toolchainResult = NativeToolchain.EmitObject(llvmModule.Text, objectPath);
        return toolchainResult.Succeeded
            ? new DependencyCompileResult(true, toolchainResult.OutputPath, [], toolchainResult)
            : new DependencyCompileResult(false, null, [], toolchainResult);
    }

    private static async Task<int> EmitTextArtifactAsync<T>(
        string? outputPath,
        TextWriter stdout,
        CompilationResult result,
        ArtifactKey<T> key,
        Func<T, string> render)
    {
        if (!result.Artifacts.TryGet(key, out T? artifact) || artifact is null)
        {
            return 1;
        }

        var text = render(artifact);
        if (outputPath is not null)
        {
            await File.WriteAllTextAsync(Path.GetFullPath(outputPath), text);
        }
        else
        {
            await stdout.WriteLineAsync(text);
        }

        return 0;
    }

    private static bool TryParseMode(string argument, out CliMode mode)
    {
        mode = argument switch
        {
            "--check" => CliMode.Check,
            "--emit-mir" => CliMode.EmitMir,
            "--emit-ssa" => CliMode.EmitSsa,
            "--emit-llvm" => CliMode.EmitLlvmIr,
            "--emit-obj" => CliMode.EmitObject,
            "--emit-lib" => CliMode.EmitLibrary,
            "--emit-exe" => CliMode.EmitExecutable,
            _ => CliMode.Default
        };

        return mode != CliMode.Default;
    }

    private static string? ResolveStopAfterPassId(CliMode mode)
    {
        return mode switch
        {
            CliMode.Check => "ownership-validate",
            CliMode.EmitMir => "lower-mir",
            CliMode.EmitSsa => "lower-ssa",
            _ => null
        };
    }

    private static string DeriveExecutableOutputPath(string? inputPath, CompilationResult result)
    {
        if (inputPath is not null)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var directory = Path.GetDirectoryName(fullInputPath) ?? Environment.CurrentDirectory;
            return Path.Combine(directory, Path.GetFileNameWithoutExtension(fullInputPath));
        }

        if (result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
            && syntaxModel is not null
            && !string.IsNullOrWhiteSpace(syntaxModel.ModuleName))
        {
            return Path.GetFullPath(syntaxModel.ModuleName);
        }

        return Path.GetFullPath("a.out");
    }

    private static string DeriveObjectOutputPath(string? inputPath, CompilationResult result)
    {
        var extension = OperatingSystem.IsWindows() ? ".obj" : ".o";

        if (inputPath is not null)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var directory = Path.GetDirectoryName(fullInputPath) ?? Environment.CurrentDirectory;
            return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fullInputPath)}{extension}");
        }

        if (result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
            && syntaxModel is not null
            && !string.IsNullOrWhiteSpace(syntaxModel.ModuleName))
        {
            return Path.GetFullPath($"{syntaxModel.ModuleName}{extension}");
        }

        return Path.GetFullPath($"a{extension}");
    }

    private static string DeriveLibraryOutputPath(string? inputPath, CompilationResult result)
    {
        var extension = OperatingSystem.IsWindows() ? ".lib" : ".a";

        if (inputPath is not null)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var directory = Path.GetDirectoryName(fullInputPath) ?? Environment.CurrentDirectory;
            return Path.Combine(directory, $"lib{Path.GetFileNameWithoutExtension(fullInputPath)}{extension}");
        }

        if (result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
            && syntaxModel is not null
            && !string.IsNullOrWhiteSpace(syntaxModel.ModuleName))
        {
            return Path.GetFullPath($"lib{syntaxModel.ModuleName}{extension}");
        }

        return Path.GetFullPath($"libstark{extension}");
    }

    private static string DeriveLibraryManifestPath(string libraryOutputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(libraryOutputPath)) ?? Environment.CurrentDirectory;
        var baseName = Path.GetFileNameWithoutExtension(libraryOutputPath);
        return Path.Combine(directory, $"{baseName}.starkpkg.json");
    }

    private enum CliMode
    {
        Default,
        Check,
        EmitMir,
        EmitSsa,
        EmitLlvmIr,
        EmitObject,
        EmitLibrary,
        EmitExecutable
    }

    private sealed record DependencyCompileResult(
        bool Success,
        string? ObjectPath,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        NativeToolchainResult? ToolchainResult);
}
