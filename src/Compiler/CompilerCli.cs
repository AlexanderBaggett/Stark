using System.Text;
using System.Text.Json;

namespace Stark.Compiler;

internal static class CompilerCli
{
    private const string Usage = "Usage: compiler [path-to-stark-file] [--check|--emit-mir|--emit-ssa|--emit-llvm|--emit-obj|--compile-only|--emit-lib|--emit-exe|--link-only|--emit-pkg|--emit-package|--inspect-pkg|--inspect-package] [-I dir|--search-dir dir]* [-L dir|--library-dir dir]* [--link-arg arg]* [--package-library-file name] [-o output] [--target triple] [--target-data-layout layout] [--target-cpu cpu] [--target-feature feature]* [--relocation-model mode] [--code-model model] [-O0|-O1|-O2|-O3|--optimize level] [--linker tool] [--archiver tool] [--save-temps dir] [--diagnostic-format format] [--log-level level] [--log-verbosity mode] [--log-category name]* [--log-stage pass]* [--log-kind kind]*";
    private const int DiagnosticTabWidth = 4;

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
        string? targetCpu = null;
        var targetFeatures = new List<string>();
        var relocationModel = LlvmRelocationModel.Default;
        LlvmCodeModel? codeModel = null;
        var optimizationLevel = CompilerOptimizationLevel.O3;
        string? linkerTool = null;
        string? archiverTool = null;
        string? saveTempsDirectory = null;
        string? packageLibraryFile = null;
        var diagnosticFormat = DiagnosticOutputFormat.Text;
        var logLevel = DiagnosticSeverity.Warning;
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

            if (TryReadOptionValue(argument, "--target-cpu", args, ref index, out var targetCpuValue))
            {
                if (string.IsNullOrWhiteSpace(targetCpuValue))
                {
                    await stderr.WriteLineAsync("Target CPU must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                targetCpu = targetCpuValue.Trim();
                continue;
            }

            if (TryReadOptionValue(argument, "--target-feature", args, ref index, out var targetFeatureValue))
            {
                if (string.IsNullOrWhiteSpace(targetFeatureValue))
                {
                    await stderr.WriteLineAsync("Target feature must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                targetFeatures.Add(targetFeatureValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--relocation-model", args, ref index, out var relocationModelValue))
            {
                if (!TryParseRelocationModel(relocationModelValue, out relocationModel))
                {
                    await stderr.WriteLineAsync($"Unknown relocation model '{relocationModelValue}'. Expected default, static, pic, or pie.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                continue;
            }

            if (TryReadOptionValue(argument, "--code-model", args, ref index, out var codeModelValue))
            {
                if (!TryParseCodeModel(codeModelValue, out codeModel))
                {
                    await stderr.WriteLineAsync($"Unknown code model '{codeModelValue}'. Expected tiny, small, kernel, medium, or large.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                continue;
            }

            if (TryParseOptimizationLevelArgument(argument, out var shortOptimizationLevel))
            {
                optimizationLevel = shortOptimizationLevel;
                continue;
            }

            if (TryReadOptionValue(argument, "--optimize", args, ref index, out var optimizeValue)
                || TryReadOptionValue(argument, "-O", args, ref index, out optimizeValue))
            {
                if (!TryParseOptimizationLevel(optimizeValue, out optimizationLevel))
                {
                    await stderr.WriteLineAsync($"Unknown optimization level '{optimizeValue}'. Expected 0, 1, 2, or 3.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

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

            if (TryReadOptionValue(argument, "--package-library-file", args, ref index, out var packageLibraryValue))
            {
                if (string.IsNullOrWhiteSpace(packageLibraryValue))
                {
                    await stderr.WriteLineAsync("Package library file name must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                packageLibraryFile = packageLibraryValue.Trim();
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

            if (TryReadOptionValue(argument, "--diagnostic-format", args, ref index, out var diagnosticFormatValue))
            {
                if (!TryParseDiagnosticFormat(diagnosticFormatValue, out diagnosticFormat))
                {
                    await stderr.WriteLineAsync($"Unknown diagnostic format '{diagnosticFormatValue}'. Expected text or json.");
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

        if (mode == CliMode.InspectPackage)
        {
            return await InspectPackageImageAsync(inputPath, outputPath, stdin, stdout, stderr, diagnosticFormat);
        }

        var requiresTargetInfo = mode is CliMode.EmitLlvmIr or CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable;
        var targetInfo = ResolveTargetInfo(
            requiresTargetInfo,
            targetTriple,
            targetDataLayout,
            targetCpu,
            targetFeatures,
            relocationModel,
            codeModel);
        var source = inputPath is not null
            ? await File.ReadAllTextAsync(inputPath)
            : await stdin.ReadToEndAsync();

        var moduleResolver = ResolveModuleResolver(inputPath, searchDirectories, targetInfo);
        var pipeline = DefaultCompilerPipeline.Create();
        var compilerOptions = new CompilerOptions(
            EmitLlvmIr: mode is CliMode.EmitLlvmIr or CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable,
            TargetInfo: targetInfo,
            StopAfterPassId: ResolveStopAfterPassId(mode),
            ModuleResolver: moduleResolver,
            QualifyModuleSymbols: mode == CliMode.EmitLibrary,
            OptimizationLevel: optimizationLevel);
        var toolchainOptions = new ToolchainCliOptions(
            linkerTool,
            archiverTool,
            librarySearchDirectories,
            linkArguments,
            saveTempsDirectory);
        using var logOutputScope = diagnosticFormat == DiagnosticOutputFormat.Json
            ? CompilerLogOutput.Push(TextWriter.Null, DiagnosticSeverity.Error)
            : CompilerLogOutput.Push(stderr, logLevel, logVerbosity, logCategories, logStages, logKinds);
        var result = pipeline.Run(
            new CompilationInput(source, inputPath),
            compilerOptions);

        if (!result.Succeeded)
        {
            await WriteDiagnosticsAsync(stderr, result.Diagnostics, diagnosticFormat, succeeded: false, source, inputPath);
            return 1;
        }

        await WriteDiagnosticsAsync(stderr, result.Diagnostics, diagnosticFormat, succeeded: true, source, inputPath);

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
                return await EmitObjectAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions, toolchainOptions);
            case CliMode.EmitLibrary:
                return await EmitLibraryAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions, toolchainOptions, diagnosticFormat);
            case CliMode.EmitExecutable:
                return await EmitExecutableAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions, toolchainOptions, diagnosticFormat);
            case CliMode.EmitPackage:
                return await EmitPackageImageAsync(outputPath, inputPath, stdout, stderr, result, packageLibraryFile, diagnosticFormat);
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
        ToolchainCliOptions toolchainOptions,
        DiagnosticOutputFormat diagnosticFormat)
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
            var rootObjectResult = NativeToolchain.EmitObject(
                llvmModule.Text,
                rootObjectPath,
                preservedLlvmOutputPath: rootLlvmPath,
                targetInfo: compilerOptions.TargetInfo,
                optimizationLevel: compilerOptions.OptimizationLevel);
            if (!rootObjectResult.Succeeded)
            {
                await WriteToolchainFailureAsync(stdout, stderr, rootObjectResult);
                return 1;
            }

            linkInputs.Add(rootObjectResult.OutputPath);
            var requiresMathLibrary = TargetRequiresExplicitMathLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresMathLibrary(llvmModule.Text);

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
                        await WriteDiagnosticsAsync(stderr, dependencyResult.Diagnostics, diagnosticFormat, succeeded: false);

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

                    requiresMathLibrary |= dependencyResult.RequiresMathLibrary;
                }
            }

            var linkArguments = BuildImplicitLinkArguments(toolchainOptions.LinkArguments, requiresMathLibrary);
            var toolchainResult = NativeToolchain.LinkExecutable(
                linkInputs,
                resolvedOutputPath,
                toolchainOptions.LinkerTool,
                toolchainOptions.LibrarySearchDirectories,
                linkArguments,
                compilerOptions.TargetInfo);
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
        ToolchainCliOptions toolchainOptions,
        DiagnosticOutputFormat diagnosticFormat)
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
            var rootObjectResult = NativeToolchain.EmitObject(
                llvmModule.Text,
                rootObjectPath,
                preservedLlvmOutputPath: rootLlvmPath,
                targetInfo: compilerOptions.TargetInfo,
                optimizationLevel: compilerOptions.OptimizationLevel);
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
                        await WriteDiagnosticsAsync(stderr, dependencyResult.Diagnostics, diagnosticFormat, succeeded: false);

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
            var manifest = PackageImageBuilder.Create(result, toolchainResult.OutputPath);
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
        CompilerOptions compilerOptions,
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
        var toolchainResult = NativeToolchain.EmitObject(
            llvmModule.Text,
            resolvedOutputPath,
            preservedLlvmOutputPath: preservedLlvmPath,
            targetInfo: compilerOptions.TargetInfo,
            optimizationLevel: compilerOptions.OptimizationLevel);
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

    private static async Task<int> EmitPackageImageAsync(
        string? outputPath,
        string? inputPath,
        TextWriter stdout,
        TextWriter stderr,
        CompilationResult result,
        string? packageLibraryFile,
        DiagnosticOutputFormat diagnosticFormat)
    {
        var packageLibraryFileName = ResolvePackageLibraryFileName(packageLibraryFile, inputPath, result);
        var packageImage = PackageImageBuilder.Create(result, packageLibraryFileName);
        var diagnostics = PackageImageLoader.ValidateManifest(packageImage, inputPath);
        if (diagnostics.Count > 0)
        {
            await WriteDiagnosticsAsync(stderr, diagnostics, diagnosticFormat, succeeded: false);
            if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                return 1;
            }
        }

        var resolvedOutputPath = Path.GetFullPath(outputPath ?? DerivePackageImageOutputPath(inputPath, result, packageLibraryFileName));
        await File.WriteAllTextAsync(resolvedOutputPath, packageImage.ToJson());
        await stdout.WriteLineAsync($"Emitted package image: {resolvedOutputPath}");
        await stdout.WriteLineAsync($"Package library file: {packageImage.LibraryFileName}");
        return 0;
    }

    private static async Task<int> InspectPackageImageAsync(
        string? inputPath,
        string? outputPath,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        DiagnosticOutputFormat diagnosticFormat)
    {
        string json;
        string sourceName;

        if (inputPath is not null)
        {
            var fullPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullPath))
            {
                await stderr.WriteLineAsync($"Package image file '{fullPath}' does not exist.");
                await stderr.WriteLineAsync(Usage);
                return 1;
            }

            json = await File.ReadAllTextAsync(fullPath);
            sourceName = fullPath;
        }
        else
        {
            json = await stdin.ReadToEndAsync();
            sourceName = "<stdin>";
        }

        if (!PackageImageLoader.TryParseManifestJson(json, sourceName, out var manifest, out var diagnostics))
        {
            await WriteDiagnosticsAsync(stderr, diagnostics, diagnosticFormat, succeeded: false);
            return 1;
        }

        if (diagnostics.Count > 0)
        {
            await WriteDiagnosticsAsync(stderr, diagnostics, diagnosticFormat, succeeded: false);
            if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                return 1;
            }
        }

        var inspection = RenderPackageImageInspection(sourceName, manifest);
        if (outputPath is not null)
        {
            await File.WriteAllTextAsync(Path.GetFullPath(outputPath), inspection);
        }
        else
        {
            await stdout.WriteLineAsync(inspection);
        }

        return 0;
    }

    private static string RenderPackageImageInspection(string sourceName, StarkPackageManifest manifest)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"package image: {sourceName}");
        builder.AppendLine($"root module: {manifest.RootModule}");
        builder.AppendLine($"library file: {manifest.LibraryFileName}");
        builder.AppendLine($"module count: {manifest.Modules.Count}");

        foreach (var module in manifest.Modules.OrderBy(static module => module.ModuleName, StringComparer.Ordinal))
        {
            var sourceSurface = module.EffectiveSourceSurface;
            var typedInterface = module.EffectiveTypedInterface;
            var compilerFacts = module.EffectiveCompilerFacts;
            var genericTemplates = module.EffectiveGenericTemplates;

            builder.AppendLine($"module {module.ModuleName}:");
            builder.AppendLine(
                $"  source-surface imports={sourceSurface.Imports?.Count ?? 0}, reexports={sourceSurface.ReExports?.Count ?? 0}, functions={sourceSurface.Functions?.Count ?? 0}, types={sourceSurface.Types?.Count ?? 0}, globals={sourceSurface.Globals?.Count ?? 0}, aliases={sourceSurface.TypeAliases?.Count ?? 0}");
            builder.AppendLine(
                $"  typed-interface functions={typedInterface?.Functions.Count ?? 0}, types={typedInterface?.Types.Count ?? 0}, globals={typedInterface?.Globals.Count ?? 0}, aliases={typedInterface?.TypeAliases?.Count ?? 0}");
            builder.AppendLine(
                $"  compiler-facts effects={compilerFacts?.FunctionEffects?.Count ?? 0}, abi={compilerFacts?.AbiFunctions?.Count ?? 0}, layouts={compilerFacts?.ConcreteLayouts?.Count ?? 0}, enum-layouts={compilerFacts?.EnumLayouts?.Count ?? 0}, semantics={compilerFacts?.FunctionSemantics?.Count ?? 0}");
            builder.AppendLine(
                $"  generic-templates functions={genericTemplates?.Functions.Count ?? 0}");
        }

        return builder.ToString().TrimEnd();
    }

    private static IModuleResolver? ResolveModuleResolver(string? inputPath, IReadOnlyList<string> searchDirectories, LlvmTargetInfo? targetInfo)
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

        if (resolvedDirectories.Count == 0)
        {
            return null;
        }

        IModuleSourceResolver resolver = new FileSystemModuleResolver(resolvedDirectories);
        if (targetInfo is not null)
        {
            resolver = new TargetAwareStdLibModuleResolver(resolver, resolvedDirectories, targetInfo);
        }

        return resolver;
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

    private static IReadOnlyList<string> BuildImplicitLinkArguments(
        IReadOnlyList<string> explicitArguments,
        bool requiresMathLibrary)
    {
        if (!requiresMathLibrary || explicitArguments.Contains("-lm", StringComparer.Ordinal))
        {
            return explicitArguments;
        }

        var combined = explicitArguments.ToList();
        combined.Add("-lm");
        return combined;
    }

    private static bool TargetRequiresExplicitMathLibrary(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo?.Triple is { Length: > 0 } triple)
        {
            return !triple.Contains("windows", StringComparison.OrdinalIgnoreCase);
        }

        return !OperatingSystem.IsWindows();
    }

    private static bool LlvmTextRequiresMathLibrary(string llvmText)
    {
        ReadOnlySpan<string> intrinsicNames =
        [
            "@llvm.acos.",
            "@llvm.asin.",
            "@llvm.atan.",
            "@llvm.atan2.",
            "@llvm.cos.",
            "@llvm.cosh.",
            "@llvm.exp.",
            "@llvm.exp2.",
            "@llvm.log.",
            "@llvm.log10.",
            "@llvm.log2.",
            "@llvm.pow.",
            "@llvm.sin.",
            "@llvm.sincos.",
            "@llvm.sinh.",
            "@llvm.tan.",
            "@llvm.tanh."
        ];

        foreach (var intrinsicName in intrinsicNames)
        {
            if (llvmText.Contains(intrinsicName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyCompileResult CompileDependencyObject(
        LoadedModuleDocument module,
        CompilerOptions rootOptions,
        string intermediateDirectory,
        bool preserveTemps)
    {
        if (rootOptions.ModuleResolver is not IModuleSourceResolver sourceResolver
            || !sourceResolver.TryLoadModuleSource(module.Reference, out var sourceText, out var sourceFilePath))
        {
            if (module.Reference.FilePath is null || !File.Exists(module.Reference.FilePath))
            {
                return new DependencyCompileResult(false, null, [], [], null, RequiresMathLibrary: false);
            }

            sourceText = File.ReadAllText(module.Reference.FilePath);
            sourceFilePath = module.Reference.FilePath;
        }

        var dependencyPipeline = DefaultCompilerPipeline.Create();
        var dependencyResult = dependencyPipeline.Run(
            new CompilationInput(
                sourceText,
                sourceFilePath ?? module.Reference.FilePath),
            rootOptions with
            {
                EmitLlvmIr = true,
                StopAfterPassId = null,
                QualifyModuleSymbols = true
            });

        if (!dependencyResult.Succeeded)
        {
            return new DependencyCompileResult(false, null, dependencyResult.Diagnostics, dependencyResult.Logs, null, RequiresMathLibrary: false);
        }

        if (!dependencyResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            return new DependencyCompileResult(false, null, [], dependencyResult.Logs, null, RequiresMathLibrary: false);
        }

        var requiresMathLibrary = LlvmTextRequiresMathLibrary(llvmModule.Text);

        var objectPath = Path.Combine(
            intermediateDirectory,
            $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
        var llvmPath = preserveTemps
            ? Path.Combine(intermediateDirectory, $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}.ll")
            : null;
        var toolchainResult = NativeToolchain.EmitObject(
            llvmModule.Text,
            objectPath,
            preservedLlvmOutputPath: llvmPath,
            targetInfo: rootOptions.TargetInfo,
            optimizationLevel: rootOptions.OptimizationLevel);
        return toolchainResult.Succeeded
            ? new DependencyCompileResult(true, toolchainResult.OutputPath, [], dependencyResult.Logs, toolchainResult, requiresMathLibrary)
            : new DependencyCompileResult(false, null, [], dependencyResult.Logs, toolchainResult, RequiresMathLibrary: false);
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
        await stdout.WriteLineAsync("  (default)      Run the full compilation pipeline and print a pass summary");
        await stdout.WriteLineAsync("  --check       Validate through ownership/lifetime analysis");
        await stdout.WriteLineAsync("  --emit-mir    Print lowered MIR");
        await stdout.WriteLineAsync("  --emit-ssa    Print lowered SSA");
        await stdout.WriteLineAsync("  --emit-llvm   Print emitted LLVM IR");
        await stdout.WriteLineAsync("  --emit-obj    Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --compile-only Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --emit-lib    Build a static library and Stark package manifest");
        await stdout.WriteLineAsync("  --emit-pkg    Emit a Stark package image without linker/archive steps");
        await stdout.WriteLineAsync("  --emit-package Emit a Stark package image without linker/archive steps");
        await stdout.WriteLineAsync("  --inspect-pkg Inspect and validate a Stark package image");
        await stdout.WriteLineAsync("  --inspect-package Inspect and validate a Stark package image");
        await stdout.WriteLineAsync("  --emit-exe    Build a native executable");
        await stdout.WriteLineAsync("  --link-only   Build a native executable from the current compilation output");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Inputs and Outputs:");
        await stdout.WriteLineAsync("  [path]                 Read a Stark source file instead of stdin");
        await stdout.WriteLineAsync("  -o <path>              Write the selected output artifact to <path>");
        await stdout.WriteLineAsync("  --package-library-file <name>  Library file name stored in emitted package images");
        await stdout.WriteLineAsync("  -I, --search-dir <dir> Add a Stark module/package search directory");
        await stdout.WriteLineAsync("  -L, --library-dir <dir> Add a native library search directory for linking");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Targeting and Native Toolchain:");
        await stdout.WriteLineAsync("  --target <triple>              Override the LLVM target triple");
        await stdout.WriteLineAsync("  --target-data-layout <layout>  Override the LLVM target data layout");
        await stdout.WriteLineAsync("  --target-cpu <cpu>             Forward an explicit target CPU to native codegen (for example: znver4)");
        await stdout.WriteLineAsync("  --target-feature <feature>     Forward a target feature string; repeatable (for example: +sse4.1)");
        await stdout.WriteLineAsync("  --relocation-model <default|static|pic|pie>  Choose the native relocation/PIC model");
        await stdout.WriteLineAsync("  --code-model <tiny|small|kernel|medium|large>  Forward an explicit LLVM code model");
        await stdout.WriteLineAsync("  -O0|-O1|-O2|-O3                Select the optimization level for frontend/codegen behavior (default: -O3)");
        await stdout.WriteLineAsync("  --optimize <0|1|2|3>           Long-form optimization level control");
        await stdout.WriteLineAsync("  --linker <tool>                Override the executable linker tool");
        await stdout.WriteLineAsync("  --archiver <tool>              Override the static library archiver tool");
        await stdout.WriteLineAsync("  --link-arg <arg>               Pass an additional argument through to the linker");
        await stdout.WriteLineAsync("  --save-temps <dir>             Preserve intermediate LLVM and object files in <dir>");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Compiler Logs:");
        await stdout.WriteLineAsync("  --diagnostic-format <text|json>      Choose text diagnostics or a stable JSON diagnostic document (default: text)");
        await stdout.WriteLineAsync("  --log-level <info|warning|error>     Set the minimum compiler log severity printed to stderr (default: warning)");
        await stdout.WriteLineAsync("  --log-verbosity <normal|verbose>     Choose low-noise normal output or richer verbose output");
        await stdout.WriteLineAsync("  --log-category <name>                Print only matching compiler log categories (repeatable)");
        await stdout.WriteLineAsync("  --log-stage <pass-id>                Print only matching compiler pass stages (repeatable)");
        await stdout.WriteLineAsync("  --log-kind <pipeline|symbol|decision|gap>  Print only matching compiler log kinds (repeatable)");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Notes:");
        await stdout.WriteLineAsync("  --emit-obj is compile-only.");
        await stdout.WriteLineAsync("  --emit-lib and --emit-exe perform link/archive steps.");
        await stdout.WriteLineAsync("  --emit-pkg validates and emits package image JSON only.");
        await stdout.WriteLineAsync("  --inspect-pkg accepts a .starkpkg.json file path or JSON from stdin.");
        await stdout.WriteLineAsync("  With no workflow flag, the compiler runs the full pipeline and prints a success summary.");
        await stdout.WriteLineAsync("  --diagnostic-format json suppresses the text compiler log stream so stderr stays machine-readable.");
        await stdout.WriteLineAsync("  Library/package search uses -I/--search-dir and the STARK_PATH environment variable.");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Examples:");
        await stdout.WriteLineAsync("  compiler app.stark --check");
        await stdout.WriteLineAsync("  compiler app.stark --emit-llvm -o app.ll");
        await stdout.WriteLineAsync("  compiler app.stark --emit-exe -o app");
        await stdout.WriteLineAsync("  compiler app.stark --emit-pkg -o app.starkpkg.json");
        await stdout.WriteLineAsync("  compiler libFacade.starkpkg.json --inspect-pkg");
        await stdout.WriteLineAsync("  compiler app.stark --diagnostic-format json");
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
            "--emit-pkg" or "--emit-package" => CliMode.EmitPackage,
            "--inspect-pkg" or "--inspect-package" => CliMode.InspectPackage,
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

    private static LlvmTargetInfo? ResolveTargetInfo(
        bool requiresTargetInfo,
        string? targetTriple,
        string? targetDataLayout,
        string? targetCpu,
        IReadOnlyList<string> targetFeatures,
        LlvmRelocationModel relocationModel,
        LlvmCodeModel? codeModel)
    {
        if (!requiresTargetInfo)
        {
            return null;
        }

        var explicitTargetInfo = CreateTargetInfo(targetTriple, targetDataLayout, targetCpu, targetFeatures, relocationModel, codeModel);
        if (explicitTargetInfo is not null)
        {
            return explicitTargetInfo;
        }

        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var detectedTargetInfo))
        {
            return null;
        }

        return detectedTargetInfo with
        {
            DataLayout = string.IsNullOrWhiteSpace(targetDataLayout) ? detectedTargetInfo.DataLayout : targetDataLayout,
            Cpu = string.IsNullOrWhiteSpace(targetCpu) ? detectedTargetInfo.Cpu : targetCpu,
            Features = targetFeatures.Count == 0 ? detectedTargetInfo.Features : targetFeatures.ToArray(),
            RelocationModel = relocationModel,
            CodeModel = codeModel
        };
    }

    private static LlvmTargetInfo? CreateTargetInfo(
        string? targetTriple,
        string? targetDataLayout,
        string? targetCpu,
        IReadOnlyList<string> targetFeatures,
        LlvmRelocationModel relocationModel,
        LlvmCodeModel? codeModel)
    {
        if (string.IsNullOrWhiteSpace(targetTriple))
        {
            return null;
        }

        return new LlvmTargetInfo(
            targetTriple,
            string.IsNullOrWhiteSpace(targetDataLayout) ? null : targetDataLayout,
            string.IsNullOrWhiteSpace(targetCpu) ? null : targetCpu,
            targetFeatures.Count == 0 ? null : targetFeatures.ToArray(),
            relocationModel,
            codeModel);
    }

    private static string? ResolveStopAfterPassId(CliMode mode)
    {
        return mode switch
        {
            CliMode.Check => "ownership-validate",
            CliMode.EmitMir => "lower-mir",
            CliMode.EmitSsa => "const-prop",
            CliMode.EmitPackage => "lower-abi",
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

    private static bool TryParseDiagnosticFormat(string value, out DiagnosticOutputFormat format)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "text":
                format = DiagnosticOutputFormat.Text;
                return true;
            case "json":
                format = DiagnosticOutputFormat.Json;
                return true;
            default:
                format = DiagnosticOutputFormat.Text;
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

    private static string ResolvePackageLibraryFileName(string? requestedLibraryFile, string? inputPath, CompilationResult result)
    {
        if (!string.IsNullOrWhiteSpace(requestedLibraryFile))
        {
            return Path.GetFileName(requestedLibraryFile.Trim());
        }

        return Path.GetFileName(DeriveLibraryOutputPath(inputPath, result));
    }

    private static string DerivePackageImageOutputPath(string? inputPath, CompilationResult result, string packageLibraryFileName)
    {
        var packageImageFileName = $"{Path.GetFileNameWithoutExtension(packageLibraryFileName)}.starkpkg.json";

        if (inputPath is not null)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var directory = Path.GetDirectoryName(fullInputPath) ?? Environment.CurrentDirectory;
            return Path.Combine(directory, packageImageFileName);
        }

        return Path.GetFullPath(packageImageFileName);
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

    private static bool TryParseOptimizationLevelArgument(string argument, out CompilerOptimizationLevel optimizationLevel)
    {
        optimizationLevel = CompilerOptimizationLevel.O3;

        if (argument.Length != 3 || !argument.StartsWith("-O", StringComparison.Ordinal))
        {
            return false;
        }

        return TryParseOptimizationLevel(argument[2..], out optimizationLevel);
    }

    private static bool TryParseOptimizationLevel(string value, out CompilerOptimizationLevel optimizationLevel)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "0":
            case "O0":
                optimizationLevel = CompilerOptimizationLevel.O0;
                return true;
            case "1":
            case "O1":
                optimizationLevel = CompilerOptimizationLevel.O1;
                return true;
            case "2":
            case "O2":
                optimizationLevel = CompilerOptimizationLevel.O2;
                return true;
            case "3":
            case "O3":
                optimizationLevel = CompilerOptimizationLevel.O3;
                return true;
            default:
                optimizationLevel = CompilerOptimizationLevel.O3;
                return false;
        }
    }

    private static bool TryParseRelocationModel(string value, out LlvmRelocationModel relocationModel)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "default":
                relocationModel = LlvmRelocationModel.Default;
                return true;
            case "static":
                relocationModel = LlvmRelocationModel.Static;
                return true;
            case "pic":
                relocationModel = LlvmRelocationModel.Pic;
                return true;
            case "pie":
                relocationModel = LlvmRelocationModel.Pie;
                return true;
            default:
                relocationModel = LlvmRelocationModel.Default;
                return false;
        }
    }

    private static bool TryParseCodeModel(string value, out LlvmCodeModel? codeModel)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "tiny":
                codeModel = LlvmCodeModel.Tiny;
                return true;
            case "small":
                codeModel = LlvmCodeModel.Small;
                return true;
            case "kernel":
                codeModel = LlvmCodeModel.Kernel;
                return true;
            case "medium":
                codeModel = LlvmCodeModel.Medium;
                return true;
            case "large":
                codeModel = LlvmCodeModel.Large;
                return true;
            default:
                codeModel = null;
                return false;
        }
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
        EmitExecutable,
        EmitPackage,
        InspectPackage
    }

    private sealed record DependencyCompileResult(
        bool Success,
        string? ObjectPath,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        IReadOnlyList<CompilerLogEntry> Logs,
        NativeToolchainResult? ToolchainResult,
        bool RequiresMathLibrary);

    private sealed record ToolchainCliOptions(
        string? LinkerTool,
        string? ArchiverTool,
        IReadOnlyList<string> LibrarySearchDirectories,
        IReadOnlyList<string> LinkArguments,
        string? SaveTempsDirectory);

    private static async Task WriteDiagnosticsAsync(
        TextWriter writer,
        IReadOnlyList<CompilerDiagnostic> diagnostics,
        DiagnosticOutputFormat format,
        bool succeeded,
        string? rootSourceText = null,
        string? rootInputPath = null)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        var summary = BuildDiagnosticSummary(diagnostics);
        switch (format)
        {
            case DiagnosticOutputFormat.Json:
                await writer.WriteLineAsync(FormatDiagnosticsAsJson(diagnostics, summary, succeeded));
                break;
            default:
                var snippetCache = new DiagnosticSnippetCache(rootSourceText, rootInputPath);
                for (var index = 0; index < diagnostics.Count; index++)
                {
                    var diagnostic = diagnostics[index];
                    if (diagnostic.Severity == DiagnosticSeverity.Info)
                    {
                        await WriteStandaloneDiagnosticAsync(writer, diagnostic, snippetCache);
                        continue;
                    }

                    await WritePrimaryDiagnosticAsync(writer, diagnostic, snippetCache);
                    while (index + 1 < diagnostics.Count
                        && diagnostics[index + 1].Severity == DiagnosticSeverity.Info)
                    {
                        index++;
                        await WriteRelatedNoteAsync(writer, diagnostics[index], snippetCache);
                    }
                }

                await writer.WriteLineAsync(FormatDiagnosticSummary(summary, succeeded));
                break;
        }
    }

    private static async Task WritePrimaryDiagnosticAsync(
        TextWriter writer,
        CompilerDiagnostic diagnostic,
        DiagnosticSnippetCache snippetCache)
    {
        await writer.WriteLineAsync(diagnostic.ToString());
        if (TryFormatDiagnosticSnippet(diagnostic, snippetCache, out var snippet))
        {
            await writer.WriteLineAsync(snippet);
        }
    }

    private static async Task WriteStandaloneDiagnosticAsync(
        TextWriter writer,
        CompilerDiagnostic diagnostic,
        DiagnosticSnippetCache snippetCache)
    {
        await writer.WriteLineAsync(diagnostic.ToString());
        if (TryFormatDiagnosticSnippet(diagnostic, snippetCache, out var snippet))
        {
            await writer.WriteLineAsync(snippet);
        }
    }

    private static async Task WriteRelatedNoteAsync(
        TextWriter writer,
        CompilerDiagnostic diagnostic,
        DiagnosticSnippetCache snippetCache)
    {
        await writer.WriteLineAsync($"  {FormatGroupedNoteHeader(diagnostic)}");
        if (TryFormatDiagnosticSnippet(diagnostic, snippetCache, out var snippet))
        {
            await writer.WriteLineAsync(IndentBlock(snippet, "  "));
        }
    }

    private static string FormatGroupedNoteHeader(CompilerDiagnostic diagnostic)
    {
        var stage = string.IsNullOrWhiteSpace(diagnostic.Stage)
            ? string.Empty
            : $" [{diagnostic.Stage}]";
        var location = diagnostic.Location is null
            ? string.Empty
            : $" at {diagnostic.Location}";
        return $"note{stage}{location}: {diagnostic.Message}";
    }

    private static string IndentBlock(string text, string indent)
    {
        return indent + text.Replace(Environment.NewLine, Environment.NewLine + indent, StringComparison.Ordinal);
    }

    private static DiagnosticSummary BuildDiagnosticSummary(IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        var errorCount = 0;
        var warningCount = 0;
        var infoCount = 0;

        foreach (var diagnostic in diagnostics)
        {
            switch (diagnostic.Severity)
            {
                case DiagnosticSeverity.Error:
                    errorCount++;
                    break;
                case DiagnosticSeverity.Warning:
                    warningCount++;
                    break;
                case DiagnosticSeverity.Info:
                    infoCount++;
                    break;
            }
        }

        return new DiagnosticSummary(
            diagnostics.Count,
            errorCount,
            warningCount,
            infoCount);
    }

    private static string FormatDiagnosticSummary(DiagnosticSummary summary, bool succeeded)
    {
        var label = succeeded ? "Summary" : "Failure summary";
        return $"{label}: {FormatCount(summary.ErrorCount, "error")}, {FormatCount(summary.WarningCount, "warning")}, {FormatCount(summary.InfoCount, "info")}.";
    }

    private static string FormatDiagnosticsAsJson(
        IReadOnlyList<CompilerDiagnostic> diagnostics,
        DiagnosticSummary summary,
        bool succeeded)
    {
        var document = new DiagnosticDocument(
            succeeded,
            summary,
            diagnostics
                .Select(static diagnostic => new DiagnosticRecord(
                    diagnostic.Code,
                    diagnostic.Severity.ToString().ToLowerInvariant(),
                    diagnostic.Message,
                    diagnostic.Stage,
                    diagnostic.Location is null
                        ? null
                        : new DiagnosticLocationRecord(
                            diagnostic.Location.FilePath,
                            diagnostic.Location.Line,
                            diagnostic.Location.Column,
                            diagnostic.Location.EndLine > 0 ? diagnostic.Location.EndLine : null,
                            diagnostic.Location.EndColumn > 0 ? diagnostic.Location.EndColumn : null)))
                .ToArray());

        return JsonSerializer.Serialize(
            document,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
    }

    private static string FormatCount(int count, string singularNoun)
    {
        return count == 1 ? $"1 {singularNoun}" : $"{count} {singularNoun}s";
    }

    private static bool TryFormatDiagnosticSnippet(
        CompilerDiagnostic diagnostic,
        DiagnosticSnippetCache snippetCache,
        out string snippet)
    {
        snippet = string.Empty;

        if (diagnostic.Location is not { Line: > 0, Column: > 0 } location
            || !snippetCache.TryGetLines(location, out var sourceLines))
        {
            return false;
        }

        var endLine = Math.Clamp(Math.Max(location.ResolvedEndLine, location.Line), location.Line, sourceLines.Length);
        var lineNumberWidth = endLine.ToString().Length;
        var builder = new StringBuilder();

        for (var lineNumber = location.Line; lineNumber <= endLine; lineNumber++)
        {
            var lineText = sourceLines[lineNumber - 1];
            var displayLineText = ExpandTabsForDisplay(lineText);
            var caretStartColumn = lineNumber == location.Line
                ? GetDisplayColumn(lineText, location.Column)
                : 0;
            var caretWidth = GetSpanCaretWidth(location, lineText, displayLineText, lineNumber, endLine, caretStartColumn);

            builder.Append("  ")
                .Append(lineNumber.ToString().PadLeft(lineNumberWidth))
                .Append(" | ")
                .Append(displayLineText)
                .AppendLine();
            builder.Append("  ")
                .Append(new string(' ', lineNumberWidth))
                .Append(" | ")
                .Append(new string(' ', caretStartColumn))
                .Append(new string('^', caretWidth));

            if (lineNumber < endLine)
            {
                builder.AppendLine();
            }
        }

        snippet = builder.ToString();
        return true;
    }

    private static int GetSpanCaretWidth(
        SourceLocation location,
        string lineText,
        string displayLineText,
        int lineNumber,
        int endLine,
        int caretStartColumn)
    {
        if (lineNumber == location.Line && lineNumber == endLine)
        {
            var endColumnExclusive = GetDisplayColumn(lineText, Math.Max(location.ResolvedEndColumn, location.Column) + 1);
            return Math.Max(endColumnExclusive - caretStartColumn, 1);
        }

        if (lineNumber == location.Line)
        {
            return Math.Max(displayLineText.Length - caretStartColumn, 1);
        }

        if (lineNumber == endLine)
        {
            return Math.Max(GetDisplayColumn(lineText, Math.Max(location.ResolvedEndColumn, 1) + 1), 1);
        }

        return Math.Max(displayLineText.Length, 1);
    }

    private static string ExpandTabsForDisplay(string text)
    {
        if (!text.Contains('\t', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var displayColumn = 0;
        foreach (var character in text)
        {
            if (character == '\t')
            {
                var width = GetDisplayWidth(character, displayColumn);
                builder.Append(' ', width);
                displayColumn += width;
            }
            else
            {
                builder.Append(character);
                displayColumn++;
            }
        }

        return builder.ToString();
    }

    private static int GetDisplayColumn(string text, int oneBasedColumn)
    {
        var characterCount = Math.Max(oneBasedColumn - 1, 0);
        var boundedCharacterCount = Math.Min(characterCount, text.Length);
        var displayColumn = 0;
        for (var index = 0; index < boundedCharacterCount; index++)
        {
            displayColumn += GetDisplayWidth(text[index], displayColumn);
        }

        if (characterCount > text.Length)
        {
            displayColumn += characterCount - text.Length;
        }

        return displayColumn;
    }

    private static int GetDisplayWidth(char character, int displayColumn)
    {
        if (character != '\t')
        {
            return 1;
        }

        var remainder = displayColumn % DiagnosticTabWidth;
        return remainder == 0 ? DiagnosticTabWidth : DiagnosticTabWidth - remainder;
    }

    private enum DiagnosticOutputFormat
    {
        Text,
        Json
    }

    private sealed record DiagnosticSummary(
        int TotalCount,
        int ErrorCount,
        int WarningCount,
        int InfoCount);

    private sealed record DiagnosticDocument(
        bool Succeeded,
        DiagnosticSummary Summary,
        IReadOnlyList<DiagnosticRecord> Diagnostics);

    private sealed record DiagnosticRecord(
        string Code,
        string Severity,
        string Message,
        string? Stage,
        DiagnosticLocationRecord? Location);

    private sealed record DiagnosticLocationRecord(
        string? FilePath,
        int Line,
        int Column,
        int? EndLine,
        int? EndColumn);

    private sealed class DiagnosticSnippetCache(string? rootSourceText, string? rootInputPath)
    {
        private readonly string? _rootSourceText = rootSourceText;
        private readonly string? _rootInputPath = string.IsNullOrWhiteSpace(rootInputPath) ? null : Path.GetFullPath(rootInputPath);
        private readonly string[]? _rootLines = rootSourceText is null ? null : SplitLines(rootSourceText);
        private readonly Dictionary<string, string[]?> _linesByPath = new(StringComparer.Ordinal);

        public bool TryGetLines(SourceLocation location, out string[] lines)
        {
            lines = [];

            var resolvedLines = ResolveLines(location.FilePath);
            if (resolvedLines is null
                || location.Line < 1
                || location.Line > resolvedLines.Length)
            {
                return false;
            }

            lines = resolvedLines;
            return true;
        }

        private string[]? ResolveLines(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return _rootLines;
            }

            var fullPath = Path.GetFullPath(filePath);
            if (_rootInputPath is not null
                && string.Equals(fullPath, _rootInputPath, StringComparison.Ordinal))
            {
                return _rootLines;
            }

            if (_linesByPath.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }

            try
            {
                cached = File.Exists(fullPath)
                    ? SplitLines(File.ReadAllText(fullPath))
                    : null;
            }
            catch
            {
                cached = null;
            }

            _linesByPath[fullPath] = cached;
            return cached;
        }

        private static string[] SplitLines(string sourceText)
        {
            return sourceText.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
        }
    }

}
