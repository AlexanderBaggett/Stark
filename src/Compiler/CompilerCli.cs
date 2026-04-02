namespace Stark.Compiler;

internal static class CompilerCli
{
    private const string Usage = "Usage: compiler [path-to-stark-file] [--check|--emit-mir|--emit-ssa|--emit-llvm|--emit-obj|--compile-only|--emit-lib|--emit-exe|--link-only] [-I dir|--search-dir dir]* [-L dir|--library-dir dir]* [--link-arg arg]* [-o output] [--target triple] [--target-data-layout layout] [--linker tool] [--archiver tool] [--save-temps dir] [--log-level level] [--log-verbosity mode] [--log-category name]* [--log-stage pass]* [--log-kind kind]*";

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var mode = CliMode.Default;
        string? inputPath = null;
        string? outputPath = null;
        var searchDirectories = new List<string>();
        var librarySearchDirectories = new List<string>();
        var linkArguments = new List<string>();
        string? targetTriple = null;
        string? targetDataLayout = null;
        string? linkerTool = null;
        string? archiverTool = null;
        string? saveTempsDirectory = null;
        var logLevel = DiagnosticSeverity.Info;
        var logVerbosity = CompilerLogVerbosity.Normal;
        var logCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logKinds = new HashSet<CompilerLogKind>();
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument is "-h" or "--help")
            {
                showHelp = true;
                continue;
            }

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

            if (TryReadOptionValue(argument, "--target", args, ref index, out var targetValue))
            {
                targetTriple = targetValue;
                continue;
            }

            if (TryReadOptionValue(argument, "--target-data-layout", args, ref index, out var targetDataLayoutValue))
            {
                targetDataLayout = targetDataLayoutValue;
                continue;
            }

            if (TryReadOptionValue(argument, "--linker", args, ref index, out var linkerValue))
            {
                linkerTool = linkerValue;
                continue;
            }

            if (TryReadOptionValue(argument, "--archiver", args, ref index, out var archiverValue))
            {
                archiverTool = archiverValue;
                continue;
            }

            if (TryReadOptionValue(argument, "--link-arg", args, ref index, out var linkArgumentValue))
            {
                linkArguments.Add(linkArgumentValue);
                continue;
            }

            if (TryReadOptionValue(argument, "--save-temps", args, ref index, out var saveTempsValue))
            {
                saveTempsDirectory = saveTempsValue;
                continue;
            }

            if (TryReadOptionValue(argument, "--log-level", args, ref index, out var logLevelValue))
            {
                if (!TryParseLogLevel(logLevelValue, out logLevel))
                {
                    await stderr.WriteLineAsync($"Unknown log level '{logLevelValue}'. Expected info, warning, or error.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                continue;
            }

            if (TryReadOptionValue(argument, "--log-verbosity", args, ref index, out var logVerbosityValue))
            {
                if (!TryParseLogVerbosity(logVerbosityValue, out logVerbosity))
                {
                    await stderr.WriteLineAsync($"Unknown log verbosity '{logVerbosityValue}'. Expected normal or verbose.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                continue;
            }

            if (TryReadOptionValue(argument, "--log-category", args, ref index, out var logCategoryValue))
            {
                if (string.IsNullOrWhiteSpace(logCategoryValue))
                {
                    await stderr.WriteLineAsync("Compiler log categories must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                logCategories.Add(logCategoryValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--log-kind", args, ref index, out var logKindValue))
            {
                if (!TryParseLogKind(logKindValue, out var logKind))
                {
                    await stderr.WriteLineAsync($"Unknown log kind '{logKindValue}'. Expected pipeline, symbol, decision, or gap.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                logKinds.Add(logKind);
                continue;
            }

            if (TryReadOptionValue(argument, "--log-stage", args, ref index, out var logStageValue))
            {
                if (string.IsNullOrWhiteSpace(logStageValue))
                {
                    await stderr.WriteLineAsync("Compiler log stages must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                logStages.Add(logStageValue.Trim());
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

            if (string.Equals(argument, "-L", StringComparison.Ordinal)
                || string.Equals(argument, "--library-dir", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length)
                {
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                librarySearchDirectories.Add(args[++index]);
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

        if (showHelp)
        {
            await WriteHelpAsync(stdout);
            return 0;
        }

        var source = inputPath is not null
            ? await File.ReadAllTextAsync(inputPath)
            : await stdin.ReadToEndAsync();

        var moduleResolver = ResolveModuleResolver(inputPath, searchDirectories);
        var pipeline = DefaultCompilerPipeline.Create();
        var requiresTargetInfo = mode is CliMode.EmitLlvmIr or CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable;
        var targetInfo = requiresTargetInfo
            ? CreateTargetInfo(targetTriple, targetDataLayout)
                ?? (NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo)
                    ? detectedTargetInfo
                    : null)
                : null;
        var compilerOptions = new CompilerOptions(
            EmitLlvmIr: mode is CliMode.EmitLlvmIr or CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable,
            TargetInfo: targetInfo,
            StopAfterPassId: ResolveStopAfterPassId(mode),
            ModuleResolver: moduleResolver,
            QualifyModuleSymbols: mode == CliMode.EmitLibrary);
        var toolchainOptions = new ToolchainCliOptions(
            linkerTool,
            archiverTool,
            librarySearchDirectories,
            linkArguments,
            saveTempsDirectory);
        using var logOutputScope = CompilerLogOutput.Push(stderr, logLevel, logVerbosity, logCategories, logStages, logKinds);
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
                return await EmitTextArtifactAsync(outputPath, stdout, result, CompilerArtifactKeys.OptimizedSsaIr, ArtifactTextRenderer.Render);
            case CliMode.EmitLlvmIr:
                return await EmitTextArtifactAsync(outputPath, stdout, result, CompilerArtifactKeys.LlvmIrModule, static module => module.Text);
            case CliMode.EmitObject:
                return await EmitObjectAsync(outputPath, inputPath, stdout, stderr, result, toolchainOptions);
            case CliMode.EmitLibrary:
                return await EmitLibraryAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions, toolchainOptions);
            case CliMode.EmitExecutable:
                return await EmitExecutableAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions, toolchainOptions);
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
        CompilerOptions compilerOptions,
        ToolchainCliOptions toolchainOptions)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            await stderr.WriteLineAsync("LLVM IR was not produced.");
            return 1;
        }

        var resolvedOutputPath = outputPath ?? DeriveExecutableOutputPath(inputPath, result);
        var linkInputs = new List<string>();
        var linkedLibraries = new HashSet<string>(StringComparer.Ordinal);
        var intermediateDirectory = CreateIntermediateDirectory(toolchainOptions.SaveTempsDirectory, "stark-link-", out var cleanupDirectory);

        try
        {
            var rootObjectPath = Path.Combine(intermediateDirectory, $"root{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
            var rootLlvmPath = toolchainOptions.SaveTempsDirectory is null ? null : Path.Combine(intermediateDirectory, "root.ll");
            var rootObjectResult = NativeToolchain.EmitObject(llvmModule.Text, rootObjectPath, preservedLlvmOutputPath: rootLlvmPath);
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

                    var dependencyResult = CompileDependencyObject(module, compilerOptions, intermediateDirectory, preserveTemps: toolchainOptions.SaveTempsDirectory is not null);
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

            var toolchainResult = NativeToolchain.LinkExecutable(
                linkInputs,
                resolvedOutputPath,
                toolchainOptions.LinkerTool,
                toolchainOptions.LibrarySearchDirectories,
                toolchainOptions.LinkArguments);
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
                cleanupDirectory?.Delete(recursive: true);
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
        CompilerOptions compilerOptions,
        ToolchainCliOptions toolchainOptions)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            await stderr.WriteLineAsync("LLVM IR was not produced.");
            return 1;
        }

        var resolvedOutputPath = outputPath ?? DeriveLibraryOutputPath(inputPath, result);
        var objectPaths = new List<string>();
        var intermediateDirectory = CreateIntermediateDirectory(toolchainOptions.SaveTempsDirectory, "stark-lib-", out var cleanupDirectory);

        try
        {
            var rootObjectPath = Path.Combine(intermediateDirectory, $"root{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
            var rootLlvmPath = toolchainOptions.SaveTempsDirectory is null ? null : Path.Combine(intermediateDirectory, "root.ll");
            var rootObjectResult = NativeToolchain.EmitObject(llvmModule.Text, rootObjectPath, preservedLlvmOutputPath: rootLlvmPath);
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
                    var dependencyResult = CompileDependencyObject(module, compilerOptions, intermediateDirectory, preserveTemps: toolchainOptions.SaveTempsDirectory is not null);
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

            var toolchainResult = NativeToolchain.CreateStaticLibrary(objectPaths, resolvedOutputPath, toolchainOptions.ArchiverTool);
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
                cleanupDirectory?.Delete(recursive: true);
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
        CompilationResult result,
        ToolchainCliOptions toolchainOptions)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            await stderr.WriteLineAsync("LLVM IR was not produced.");
            return 1;
        }

        var resolvedOutputPath = outputPath ?? DeriveObjectOutputPath(inputPath, result);
        var preservedLlvmPath = toolchainOptions.SaveTempsDirectory is null
            ? null
            : Path.Combine(
                CreateIntermediateDirectory(toolchainOptions.SaveTempsDirectory, "stark-obj-", out _),
                $"{Path.GetFileNameWithoutExtension(resolvedOutputPath)}.ll");
        var toolchainResult = NativeToolchain.EmitObject(llvmModule.Text, resolvedOutputPath, preservedLlvmOutputPath: preservedLlvmPath);
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
        string intermediateDirectory,
        bool preserveTemps)
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
            return new DependencyCompileResult(false, null, dependencyResult.Diagnostics, dependencyResult.Logs, null);
        }

        if (!dependencyResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            return new DependencyCompileResult(false, null, [], dependencyResult.Logs, null);
        }

        var objectPath = Path.Combine(
            intermediateDirectory,
            $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
        var llvmPath = preserveTemps
            ? Path.Combine(intermediateDirectory, $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}.ll")
            : null;
        var toolchainResult = NativeToolchain.EmitObject(llvmModule.Text, objectPath, preservedLlvmOutputPath: llvmPath);
        return toolchainResult.Succeeded
            ? new DependencyCompileResult(true, toolchainResult.OutputPath, [], dependencyResult.Logs, toolchainResult)
            : new DependencyCompileResult(false, null, [], dependencyResult.Logs, toolchainResult);
    }

    private static string CreateIntermediateDirectory(string? requestedDirectory, string tempPrefix, out DirectoryInfo? cleanupDirectory)
    {
        if (!string.IsNullOrWhiteSpace(requestedDirectory))
        {
            var fullPath = Path.GetFullPath(requestedDirectory);
            Directory.CreateDirectory(fullPath);
            cleanupDirectory = null;
            return fullPath;
        }

        cleanupDirectory = Directory.CreateTempSubdirectory(tempPrefix);
        return cleanupDirectory.FullName;
    }

    private static async Task WriteHelpAsync(TextWriter stdout)
    {
        await stdout.WriteLineAsync(Usage);
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Workflows:");
        await stdout.WriteLineAsync("  --check       Validate through ownership/lifetime analysis");
        await stdout.WriteLineAsync("  --emit-mir    Print lowered MIR");
        await stdout.WriteLineAsync("  --emit-ssa    Print lowered SSA");
        await stdout.WriteLineAsync("  --emit-llvm   Print emitted LLVM IR");
        await stdout.WriteLineAsync("  --emit-obj    Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --compile-only Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --emit-lib    Build a static library and Stark package manifest");
        await stdout.WriteLineAsync("  --emit-exe    Build a native executable");
        await stdout.WriteLineAsync("  --link-only   Build a native executable from the current compilation output");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Inputs and Outputs:");
        await stdout.WriteLineAsync("  [path]                 Read a Stark source file instead of stdin");
        await stdout.WriteLineAsync("  -o <path>              Write the selected output artifact to <path>");
        await stdout.WriteLineAsync("  -I, --search-dir <dir> Add a Stark module/package search directory");
        await stdout.WriteLineAsync("  -L, --library-dir <dir> Add a native library search directory for linking");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Targeting and Native Toolchain:");
        await stdout.WriteLineAsync("  --target <triple>              Override the LLVM target triple");
        await stdout.WriteLineAsync("  --target-data-layout <layout>  Override the LLVM target data layout");
        await stdout.WriteLineAsync("  --linker <tool>                Override the executable linker tool");
        await stdout.WriteLineAsync("  --archiver <tool>              Override the static library archiver tool");
        await stdout.WriteLineAsync("  --link-arg <arg>               Pass an additional argument through to the linker");
        await stdout.WriteLineAsync("  --save-temps <dir>             Preserve intermediate LLVM and object files in <dir>");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Compiler Logs:");
        await stdout.WriteLineAsync("  --log-level <info|warning|error>     Set the minimum compiler log severity printed to stderr");
        await stdout.WriteLineAsync("  --log-verbosity <normal|verbose>     Choose low-noise normal output or richer verbose output");
        await stdout.WriteLineAsync("  --log-category <name>                Print only matching compiler log categories (repeatable)");
        await stdout.WriteLineAsync("  --log-stage <pass-id>                Print only matching compiler pass stages (repeatable)");
        await stdout.WriteLineAsync("  --log-kind <pipeline|symbol|decision|gap>  Print only matching compiler log kinds (repeatable)");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Notes:");
        await stdout.WriteLineAsync("  --emit-obj is compile-only.");
        await stdout.WriteLineAsync("  --emit-lib and --emit-exe perform link/archive steps.");
        await stdout.WriteLineAsync("  Library/package search uses -I/--search-dir and the STARK_PATH environment variable.");
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
            "--emit-obj" or "--compile-only" => CliMode.EmitObject,
            "--emit-lib" => CliMode.EmitLibrary,
            "--emit-exe" or "--link-only" => CliMode.EmitExecutable,
            _ => CliMode.Default
        };

        return mode != CliMode.Default;
    }

    private static bool TryReadOptionValue(string argument, string optionName, string[] args, ref int index, out string value)
    {
        if (argument.StartsWith($"{optionName}=", StringComparison.Ordinal))
        {
            value = argument[(optionName.Length + 1)..];
            return true;
        }

        if (!string.Equals(argument, optionName, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static LlvmTargetInfo? CreateTargetInfo(string? targetTriple, string? targetDataLayout)
    {
        if (string.IsNullOrWhiteSpace(targetTriple))
        {
            return null;
        }

        return new LlvmTargetInfo(targetTriple, string.IsNullOrWhiteSpace(targetDataLayout) ? null : targetDataLayout);
    }

    private static string? ResolveStopAfterPassId(CliMode mode)
    {
        return mode switch
        {
            CliMode.Check => "ownership-validate",
            CliMode.EmitMir => "lower-mir",
            CliMode.EmitSsa => "const-prop",
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

    private static bool TryParseLogVerbosity(string value, out CompilerLogVerbosity verbosity)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "normal":
                verbosity = CompilerLogVerbosity.Normal;
                return true;
            case "verbose":
                verbosity = CompilerLogVerbosity.Verbose;
                return true;
            default:
                verbosity = CompilerLogVerbosity.Normal;
                return false;
        }
    }

    private static bool TryParseLogKind(string value, out CompilerLogKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "pipeline":
                kind = CompilerLogKind.Pipeline;
                return true;
            case "symbol":
                kind = CompilerLogKind.Symbol;
                return true;
            case "decision":
                kind = CompilerLogKind.Decision;
                return true;
            case "gap":
                kind = CompilerLogKind.Gap;
                return true;
            default:
                kind = CompilerLogKind.Pipeline;
                return false;
        }
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

    private static bool TryParseLogLevel(string text, out DiagnosticSeverity severity)
    {
        severity = text.Trim().ToLowerInvariant() switch
        {
            "info" => DiagnosticSeverity.Info,
            "warning" => DiagnosticSeverity.Warning,
            "error" => DiagnosticSeverity.Error,
            _ => DiagnosticSeverity.Info
        };

        return string.Equals(text, "info", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "error", StringComparison.OrdinalIgnoreCase);
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
        IReadOnlyList<CompilerLogEntry> Logs,
        NativeToolchainResult? ToolchainResult);

    private sealed record ToolchainCliOptions(
        string? LinkerTool,
        string? ArchiverTool,
        IReadOnlyList<string> LibrarySearchDirectories,
        IReadOnlyList<string> LinkArguments,
        string? SaveTempsDirectory);

}
