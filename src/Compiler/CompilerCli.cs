using System.Text;
using System.Text.Json;

namespace Stark.Compiler;

internal static class CompilerCli
{
    private const string Usage = "Usage: compiler [path-to-stark-file] [--check|--emit-mir|--emit-ssa|--emit-llvm|--emit-obj|--compile-only|--emit-lib|--emit-exe|--link-only|--emit-pkg|--emit-package|--inspect-pkg|--inspect-package] [-I dir|--search-dir dir]* [-L dir|--library-dir dir]* [--link-arg arg]* [--native-source path]* [--native-include-dir dir]* [--native-library-dir dir]* [--native-library name]* [--native-pkg-config name]* [--native-link-arg arg]* [--package-library-file name] [-o output] [--target triple] [--target-data-layout layout] [--target-cpu cpu] [--target-feature feature]* [--relocation-model mode] [--code-model model] [-O0|-Og|-O1|-O2|-O3|--optimize level] [--linker tool] [--archiver tool] [--save-temps dir] [--toolchain-metrics path] [--diagnostic-format format] [--log-level level] [--log-verbosity mode] [--log-category name]* [--log-stage pass]* [--log-kind kind]*";
    private const int DiagnosticTabWidth = 4;

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 0 && ProjectCliDriver.IsProjectCommand(args[0]))
        {
            return await ProjectCliDriver.RunAsync(args, stdout, stderr);
        }

        var mode = CliMode.Default;
        string? inputPath = null;
        string? outputPath = null;
        var searchDirectories = new List<string>();
        var librarySearchDirectories = new List<string>();
        var linkArguments = new List<string>();
        var nativeSources = new List<string>();
        var nativeIncludeDirectories = new List<string>();
        var nativeLibraryDirectories = new List<string>();
        var nativeLibraries = new List<string>();
        var nativePkgConfigPackages = new List<string>();
        var nativeLinkArguments = new List<string>();
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
        string? toolchainMetricsPath = null;
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
                    await stderr.WriteLineAsync($"Unknown optimization level '{optimizeValue}'. Expected 0, g, 1, 2, or 3.");
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

            if (TryReadOptionValue(argument, "--native-source", args, ref index, out var nativeSourceValue))
            {
                if (string.IsNullOrWhiteSpace(nativeSourceValue))
                {
                    await stderr.WriteLineAsync("Native source path must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                nativeSources.Add(nativeSourceValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--native-include-dir", args, ref index, out var nativeIncludeDirectoryValue))
            {
                if (string.IsNullOrWhiteSpace(nativeIncludeDirectoryValue))
                {
                    await stderr.WriteLineAsync("Native include directory must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                nativeIncludeDirectories.Add(nativeIncludeDirectoryValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--native-library-dir", args, ref index, out var nativeLibraryDirectoryValue))
            {
                if (string.IsNullOrWhiteSpace(nativeLibraryDirectoryValue))
                {
                    await stderr.WriteLineAsync("Native library directory must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                nativeLibraryDirectories.Add(nativeLibraryDirectoryValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--native-library", args, ref index, out var nativeLibraryValue))
            {
                if (string.IsNullOrWhiteSpace(nativeLibraryValue))
                {
                    await stderr.WriteLineAsync("Native library name must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                nativeLibraries.Add(nativeLibraryValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--native-pkg-config", args, ref index, out var nativePkgConfigValue))
            {
                if (string.IsNullOrWhiteSpace(nativePkgConfigValue))
                {
                    await stderr.WriteLineAsync("Native pkg-config package name must not be empty.");
                    return 1;
                }

                nativePkgConfigPackages.Add(nativePkgConfigValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--native-link-arg", args, ref index, out var nativeLinkArgumentValue))
            {
                if (string.IsNullOrWhiteSpace(nativeLinkArgumentValue))
                {
                    await stderr.WriteLineAsync("Native link argument must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                nativeLinkArguments.Add(nativeLinkArgumentValue.Trim());
                continue;
            }

            if (TryReadOptionValue(argument, "--save-temps", args, ref index, out var saveTempsValue))
            {
                saveTempsDirectory = saveTempsValue;
                continue;
            }

            if (TryReadOptionValue(argument, "--toolchain-metrics", args, ref index, out var toolchainMetricsValue))
            {
                if (string.IsNullOrWhiteSpace(toolchainMetricsValue))
                {
                    await stderr.WriteLineAsync("Toolchain metrics path must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                toolchainMetricsPath = toolchainMetricsValue.Trim();
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
            OptimizationLevel: optimizationLevel,
            InternalizeModulePrivate: mode == CliMode.EmitExecutable);
        var nativeDependencies = new NativeDependencyCliOptions(
            nativeSources,
            nativeIncludeDirectories,
            nativeLibraryDirectories,
            nativeLibraries,
            nativePkgConfigPackages,
            nativeLinkArguments);
        var toolchainOptions = new ToolchainCliOptions(
            linkerTool,
            archiverTool,
            librarySearchDirectories,
            linkArguments,
            saveTempsDirectory,
            toolchainMetricsPath,
            nativeDependencies);
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
                return await EmitPackageImageAsync(outputPath, inputPath, stdout, stderr, result, packageLibraryFile, toolchainOptions.NativeDependencies, diagnosticFormat);
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
        var enableExecutableLto = ShouldEnableExecutableLto(compilerOptions.OptimizationLevel, toolchainOptions.LinkerTool)
            && ShouldEnableRootModuleLto(result)
            && !UsesPrecompiledStarkLibraries(result)
            && !LlvmTextReferencesSystemCollections(llvmModule.Text);
        var toolchainMetrics = new ToolchainMetrics();

        try
        {
            var rootObjectPath = Path.Combine(intermediateDirectory, $"root{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
            var rootLlvmPath = toolchainOptions.SaveTempsDirectory is null ? null : Path.Combine(intermediateDirectory, "root.ll");
            var rootObjectResult = NativeToolchain.EmitObject(
                llvmModule.Text,
                rootObjectPath,
                preservedLlvmOutputPath: rootLlvmPath,
                targetInfo: compilerOptions.TargetInfo,
                optimizationLevel: compilerOptions.OptimizationLevel,
                enableLto: enableExecutableLto);
            toolchainMetrics.AddLlvmObject(rootObjectResult);
            if (!rootObjectResult.Succeeded)
            {
                await WriteToolchainFailureAsync(stdout, stderr, rootObjectResult);
                return 1;
            }

            linkInputs.Add(rootObjectResult.OutputPath);
            var requiresMathLibrary = TargetRequiresExplicitMathLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresMathLibrary(llvmModule.Text);
            var requiresWinsockLibrary = TargetRequiresWinsockLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresWinsockLibrary(llvmModule.Text);
            var requiresWindowsSynchronizationLibrary = TargetRequiresWindowsSynchronizationLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresWindowsSynchronizationLibrary(llvmModule.Text);

            if (result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
                && loadedModules is not null)
            {
                var sourceDependencyModules = new List<LoadedModuleDocument>();
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

                    sourceDependencyModules.Add(module);
                }

                var sourceDependencyResult = CompileAndEmitReferencedDependencyObjects(
                    sourceDependencyModules,
                    llvmModule.Text,
                    compilerOptions,
                    intermediateDirectory,
                    preserveTemps: toolchainOptions.SaveTempsDirectory is not null,
                    toolchainMetrics: toolchainMetrics,
                    enableLto: enableExecutableLto);
                if (!sourceDependencyResult.Success)
                {
                    await WriteDiagnosticsAsync(stderr, sourceDependencyResult.Diagnostics, diagnosticFormat, succeeded: false);

                    if (sourceDependencyResult.ToolchainResult is not null)
                    {
                        await WriteToolchainFailureAsync(stdout, stderr, sourceDependencyResult.ToolchainResult);
                    }

                    return 1;
                }

                linkInputs.AddRange(sourceDependencyResult.ObjectPaths);
                requiresMathLibrary |= sourceDependencyResult.RequiresMathLibrary;
                requiresWinsockLibrary |= sourceDependencyResult.RequiresWinsockLibrary;
                requiresWindowsSynchronizationLibrary |= sourceDependencyResult.RequiresWindowsSynchronizationLibrary;
            }

            var nativeDependencyResult = CompileNativeDependenciesForExecutable(
                result,
                inputPath,
                compilerOptions,
                toolchainOptions,
                intermediateDirectory,
                toolchainMetrics);
            if (!nativeDependencyResult.Success)
            {
                await WriteDiagnosticsAsync(stderr, nativeDependencyResult.Diagnostics, diagnosticFormat, succeeded: false);

                if (nativeDependencyResult.ToolchainResult is not null)
                {
                    await WriteToolchainFailureAsync(stdout, stderr, nativeDependencyResult.ToolchainResult);
                }

                return 1;
            }

            linkInputs.AddRange(nativeDependencyResult.ObjectPaths);

            var combinedLibrarySearchDirectories = CombineDistinct(
                toolchainOptions.LibrarySearchDirectories,
                nativeDependencyResult.LibrarySearchDirectories);
            var combinedExplicitLinkArguments = CombineDistinct(
                nativeDependencyResult.LinkArguments,
                toolchainOptions.LinkArguments);
            var linkArguments = BuildImplicitLinkArguments(
                combinedExplicitLinkArguments,
                requiresMathLibrary,
                requiresWinsockLibrary,
                requiresWindowsSynchronizationLibrary,
                compilerOptions.TargetInfo);
            var toolchainResult = NativeToolchain.LinkExecutable(
                linkInputs,
                resolvedOutputPath,
                toolchainOptions.LinkerTool,
                combinedLibrarySearchDirectories,
                linkArguments,
                compilerOptions.TargetInfo,
                compilerOptions.OptimizationLevel,
                enableExecutableLto);
            toolchainMetrics.AddLink(toolchainResult);
            if (!toolchainResult.Succeeded)
            {
                await WriteDiagnosticsAsync(
                    stderr,
                    BuildMissingNativeLibraryDiagnostics(toolchainResult, combinedExplicitLinkArguments),
                    diagnosticFormat,
                    succeeded: false);
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

            await toolchainMetrics.WriteAsync(toolchainOptions.ToolchainMetricsPath);
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
        var toolchainMetrics = new ToolchainMetrics();

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
            toolchainMetrics.AddLlvmObject(rootObjectResult);
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
                    var dependencyResult = CompileDependencyObject(
                        module,
                        compilerOptions,
                        intermediateDirectory,
                        preserveTemps: toolchainOptions.SaveTempsDirectory is not null,
                        toolchainMetrics: toolchainMetrics);
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
            toolchainMetrics.AddArchive(toolchainResult);
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
            var manifest = PackageImageBuilder.Create(
                result,
                toolchainResult.OutputPath,
                toolchainOptions.NativeDependencies.ToManifest(Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJson());

            await toolchainMetrics.WriteAsync(toolchainOptions.ToolchainMetricsPath);
            await stdout.WriteLineAsync($"Emitted static library: {toolchainResult.OutputPath}");
            await stdout.WriteLineAsync($"Emitted package image: {manifestPath}");
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
        var toolchainMetrics = new ToolchainMetrics();
        var toolchainResult = NativeToolchain.EmitObject(
            llvmModule.Text,
            resolvedOutputPath,
            preservedLlvmOutputPath: preservedLlvmPath,
            targetInfo: compilerOptions.TargetInfo,
            optimizationLevel: compilerOptions.OptimizationLevel);
        toolchainMetrics.AddLlvmObject(toolchainResult);
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

        await toolchainMetrics.WriteAsync(toolchainOptions.ToolchainMetricsPath);
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
        NativeDependencyCliOptions nativeDependencies,
        DiagnosticOutputFormat diagnosticFormat)
    {
        var packageLibraryFileName = ResolvePackageLibraryFileName(packageLibraryFile, inputPath, result);
        var resolvedOutputPath = Path.GetFullPath(outputPath ?? DerivePackageImageOutputPath(inputPath, result, packageLibraryFileName));
        var packageImage = PackageImageBuilder.Create(
            result,
            packageLibraryFileName,
            nativeDependencies.ToManifest(Path.GetDirectoryName(resolvedOutputPath) ?? Environment.CurrentDirectory));
        var diagnostics = PackageImageLoader.ValidateManifest(packageImage, inputPath);
        if (diagnostics.Count > 0)
        {
            await WriteDiagnosticsAsync(stderr, diagnostics, diagnosticFormat, succeeded: false);
            if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                return 1;
            }
        }

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
        builder.AppendLine(
            $"native dependencies: sources={manifest.NativeDependencies?.Sources?.Count ?? 0}, includes={manifest.NativeDependencies?.IncludeDirectories?.Count ?? 0}, library-dirs={manifest.NativeDependencies?.LibraryDirectories?.Count ?? 0}, libraries={manifest.NativeDependencies?.Libraries?.Count ?? 0}, pkg-config={manifest.NativeDependencies?.PkgConfigPackages?.Count ?? 0}, link-args={manifest.NativeDependencies?.LinkArguments?.Count ?? 0}");

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
                $"  linkage object={compilerFacts?.Linkage?.ObjectFileName ?? "<none>"}, defines={compilerFacts?.Linkage?.DefinedSymbols.Count ?? 0}, references={compilerFacts?.Linkage?.ReferencedSymbols?.Count ?? 0}");
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
        bool requiresMathLibrary,
        bool requiresWinsockLibrary,
        bool requiresWindowsSynchronizationLibrary,
        LlvmTargetInfo? targetInfo)
    {
        if ((!requiresMathLibrary || explicitArguments.Contains("-lm", StringComparer.Ordinal))
            && (!requiresWinsockLibrary || ContainsWinsockLinkArgument(explicitArguments))
            && (!requiresWindowsSynchronizationLibrary || ContainsWindowsSynchronizationLinkArgument(explicitArguments)))
        {
            return explicitArguments;
        }

        var combined = explicitArguments.ToList();
        if (requiresMathLibrary && !explicitArguments.Contains("-lm", StringComparer.Ordinal))
        {
            combined.Add("-lm");
        }

        if (requiresWinsockLibrary && !ContainsWinsockLinkArgument(explicitArguments))
        {
            combined.Add(WinsockLinkArgument(targetInfo));
        }

        if (requiresWindowsSynchronizationLibrary && !ContainsWindowsSynchronizationLinkArgument(explicitArguments))
        {
            combined.Add(WindowsSynchronizationLinkArgument(targetInfo));
        }

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

    private static bool TargetRequiresWinsockLibrary(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo?.Triple is { Length: > 0 } triple)
        {
            return triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("win32", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase);
        }

        return OperatingSystem.IsWindows();
    }

    private static bool TargetRequiresWindowsSynchronizationLibrary(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo?.Triple is { Length: > 0 } triple)
        {
            return triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("win32", StringComparison.OrdinalIgnoreCase);
        }

        return OperatingSystem.IsWindows();
    }

    private static string WinsockLinkArgument(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo?.Triple is { Length: > 0 } triple
            && (triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("win32", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("gnu", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase)))
        {
            return "-lws2_32";
        }

        return OperatingSystem.IsWindows() ? "-lws2_32" : "Ws2_32.lib";
    }

    private static bool ContainsWinsockLinkArgument(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "Ws2_32.lib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "ws2_32.lib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-lws2_32", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string WindowsSynchronizationLinkArgument(LlvmTargetInfo? targetInfo)
    {
        _ = targetInfo;
        return "-lsynchronization";
    }

    private static bool ContainsWindowsSynchronizationLinkArgument(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "synchronization.lib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-lsynchronization", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static bool LlvmTextRequiresWinsockLibrary(string llvmText)
    {
        ReadOnlySpan<string> symbolNames =
        [
            "@WSAStartup(",
            "@WSAGetLastError(",
            "@WSASocketW(",
            "@closesocket("
        ];

        foreach (var symbolName in symbolNames)
        {
            if (llvmText.Contains(symbolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LlvmTextRequiresWindowsSynchronizationLibrary(string llvmText)
    {
        ReadOnlySpan<string> symbolNames =
        [
            "@WaitOnAddress(",
            "@WakeByAddressSingle(",
            "@WakeByAddressAll("
        ];

        foreach (var symbolName in symbolNames)
        {
            if (llvmText.Contains(symbolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static NativeDependencyLinkResult CompileNativeDependenciesForExecutable(
        CompilationResult result,
        string? inputPath,
        CompilerOptions compilerOptions,
        ToolchainCliOptions toolchainOptions,
        string intermediateDirectory,
        ToolchainMetrics? toolchainMetrics = null)
    {
        var diagnostics = new List<CompilerDiagnostic>();
        var dependencySets = new List<NativeDependencySet>();

        if (toolchainOptions.NativeDependencies.HasAny)
        {
            dependencySets.Add(new NativeDependencySet(
                PackageName: "<current compilation>",
                BaseDirectory: Environment.CurrentDirectory,
                ManifestPath: inputPath,
                Dependencies: toolchainOptions.NativeDependencies.ToManifest(Environment.CurrentDirectory)!));
        }

        if (result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
            && loadedModules is not null)
        {
            var seenManifests = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in loadedModules.ImportedModules)
            {
                if (string.IsNullOrWhiteSpace(module.Reference.ManifestPath))
                {
                    continue;
                }

                var manifestPath = Path.GetFullPath(module.Reference.ManifestPath!);
                if (!seenManifests.Add(manifestPath))
                {
                    continue;
                }

                if (!PackageImageLoader.TryLoadManifest(manifestPath, out var manifest))
                {
                    diagnostics.Add(new CompilerDiagnostic(
                        Code: "STK7200",
                        Severity: DiagnosticSeverity.Error,
                        Message: $"Package image '{manifestPath}' could not be read while gathering native dependencies.",
                        Stage: "native-link",
                        Location: new SourceLocation(manifestPath, 1, 1)));
                    continue;
                }

                if (!HasNativeDependencies(manifest.NativeDependencies))
                {
                    continue;
                }

                dependencySets.Add(new NativeDependencySet(
                    manifest.RootModule,
                    Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory,
                    manifestPath,
                    manifest.NativeDependencies!));
            }
        }

        if (diagnostics.Count != 0)
        {
            return new NativeDependencyLinkResult(false, [], [], [], diagnostics, null);
        }

        var objectPaths = new List<string>();
        var librarySearchDirectories = new List<string>();
        var linkArguments = new List<string>();
        var compiledNativeSources = new HashSet<string>(StringComparer.Ordinal);
        var objectIndex = 0;

        foreach (var dependencySet in dependencySets)
        {
            var pkgConfigResult = ResolveNativePkgConfigPackages(
                dependencySet.Dependencies.PkgConfigPackages,
                dependencySet.PackageName,
                dependencySet.ManifestPath);
            if (!pkgConfigResult.Success)
            {
                diagnostics.AddRange(pkgConfigResult.Diagnostics);
                continue;
            }

            var includeDirectories = CombineDistinct(
                ResolveNativePaths(
                    dependencySet.Dependencies.IncludeDirectories,
                    dependencySet.BaseDirectory),
                pkgConfigResult.IncludeDirectories);
            foreach (var includeDirectory in includeDirectories)
            {
                if (Directory.Exists(includeDirectory))
                {
                    continue;
                }

                diagnostics.Add(new CompilerDiagnostic(
                    Code: "STK7201",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Native include directory '{includeDirectory}' from package '{dependencySet.PackageName}' was not found.",
                    Stage: "native-link",
                    Location: new SourceLocation(dependencySet.ManifestPath, 1, 1)));
            }

            foreach (var libraryDirectory in CombineDistinct(
                         ResolveNativePaths(
                             dependencySet.Dependencies.LibraryDirectories,
                             dependencySet.BaseDirectory),
                         pkgConfigResult.LibrarySearchDirectories))
            {
                if (!Directory.Exists(libraryDirectory))
                {
                    diagnostics.Add(new CompilerDiagnostic(
                        Code: "STK7202",
                        Severity: DiagnosticSeverity.Error,
                        Message: $"Native library directory '{libraryDirectory}' from package '{dependencySet.PackageName}' was not found.",
                        Stage: "native-link",
                        Location: new SourceLocation(dependencySet.ManifestPath, 1, 1)));
                    continue;
                }

                librarySearchDirectories.Add(libraryDirectory);
            }

            linkArguments.AddRange(pkgConfigResult.LinkArguments);

            foreach (var sourcePath in ResolveNativePaths(
                         dependencySet.Dependencies.Sources,
                         dependencySet.BaseDirectory))
            {
                if (!File.Exists(sourcePath))
                {
                    diagnostics.Add(new CompilerDiagnostic(
                        Code: "STK7203",
                        Severity: DiagnosticSeverity.Error,
                        Message: $"Native source '{sourcePath}' from package '{dependencySet.PackageName}' was not found.",
                        Stage: "native-link",
                        Location: new SourceLocation(dependencySet.ManifestPath, 1, 1)));
                    continue;
                }

                if (!compiledNativeSources.Add(sourcePath))
                {
                    continue;
                }

                var objectPath = Path.Combine(
                    intermediateDirectory,
                    $"native_{objectIndex++}_{Path.GetFileNameWithoutExtension(sourcePath)}{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
                var toolchainResult = NativeToolchain.EmitNativeObject(
                    sourcePath,
                    objectPath,
                    includeDirectories,
                    compilerOptions.TargetInfo,
                    compilerOptions.OptimizationLevel);
                toolchainMetrics?.AddNativeObject(toolchainResult);
                if (!toolchainResult.Succeeded)
                {
                    return new NativeDependencyLinkResult(false, objectPaths, librarySearchDirectories, linkArguments, [], toolchainResult);
                }

                objectPaths.Add(toolchainResult.OutputPath);
            }

            foreach (var library in dependencySet.Dependencies.Libraries ?? [])
            {
                if (string.IsNullOrWhiteSpace(library))
                {
                    continue;
                }

                linkArguments.Add(FormatNativeLibraryArgument(library.Trim(), compilerOptions.TargetInfo));
            }

            foreach (var linkArgument in dependencySet.Dependencies.LinkArguments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(linkArgument))
                {
                    linkArguments.Add(linkArgument.Trim());
                }
            }
        }

        if (diagnostics.Count != 0)
        {
            return new NativeDependencyLinkResult(false, objectPaths, librarySearchDirectories, linkArguments, diagnostics, null);
        }

        return new NativeDependencyLinkResult(
            true,
            objectPaths,
            CombineDistinct(librarySearchDirectories),
            CombineDistinct(linkArguments),
            [],
            null);
    }

    private static bool HasNativeDependencies(StarkPackageNativeDependencyManifest? dependencies)
    {
        return dependencies is not null
            && ((dependencies.Sources?.Count ?? 0) != 0
                || (dependencies.IncludeDirectories?.Count ?? 0) != 0
                || (dependencies.LibraryDirectories?.Count ?? 0) != 0
                || (dependencies.Libraries?.Count ?? 0) != 0
                || (dependencies.PkgConfigPackages?.Count ?? 0) != 0
                || (dependencies.LinkArguments?.Count ?? 0) != 0);
    }

    private static IReadOnlyList<string> ResolveNativePaths(IReadOnlyList<string>? paths, string baseDirectory)
    {
        if (paths is not { Count: > 0 })
        {
            return [];
        }

        return paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ResolveNativePath(baseDirectory, path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveNativePath(string baseDirectory, string path)
    {
        var trimmed = path.Trim();
        return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(baseDirectory, trimmed));
    }

    private static NativePkgConfigResolveResult ResolveNativePkgConfigPackages(
        IReadOnlyList<string>? packages,
        string packageName,
        string? manifestPath)
    {
        if (packages is not { Count: > 0 })
        {
            return NativePkgConfigResolveResult.Successful([], [], []);
        }

        var names = packages
            .Where(static package => !string.IsNullOrWhiteSpace(package))
            .Select(static package => package.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0)
        {
            return NativePkgConfigResolveResult.Successful([], [], []);
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pkg-config",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--cflags");
        startInfo.ArgumentList.Add("--libs");
        foreach (var name in names)
        {
            startInfo.ArgumentList.Add(name);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return NativePkgConfigResolveResult.Failed(BuildPkgConfigDiagnostic(
                    names,
                    packageName,
                    manifestPath,
                    "pkg-config could not be started."));
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? "pkg-config did not find the requested package." : stderr.Trim();
                return NativePkgConfigResolveResult.Failed(BuildPkgConfigDiagnostic(names, packageName, manifestPath, detail));
            }

            return ParsePkgConfigFlags(stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return NativePkgConfigResolveResult.Failed(BuildPkgConfigDiagnostic(
                names,
                packageName,
                manifestPath,
                "pkg-config is not available on PATH."));
        }
    }

    private static CompilerDiagnostic BuildPkgConfigDiagnostic(
        IReadOnlyList<string> packageNames,
        string packageName,
        string? manifestPath,
        string detail)
    {
        return new CompilerDiagnostic(
            Code: "STK7205",
            Severity: DiagnosticSeverity.Error,
            Message:
                $"Native pkg-config package '{string.Join(", ", packageNames)}' from package '{packageName}' could not be resolved. Install it for this target, set PKG_CONFIG_PATH, or provide explicit native include/library metadata instead. {detail}",
            Stage: "native-link",
            Location: string.IsNullOrWhiteSpace(manifestPath) ? null : new SourceLocation(manifestPath, 1, 1));
    }

    private static NativePkgConfigResolveResult ParsePkgConfigFlags(string text)
    {
        var includeDirectories = new List<string>();
        var librarySearchDirectories = new List<string>();
        var linkArguments = new List<string>();
        var tokens = SplitPkgConfigFlags(text);

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Length == 0)
            {
                continue;
            }

            if (token == "-I" && index + 1 < tokens.Count)
            {
                includeDirectories.Add(Path.GetFullPath(tokens[++index]));
                continue;
            }

            if (token.StartsWith("-I", StringComparison.Ordinal) && token.Length > 2)
            {
                includeDirectories.Add(Path.GetFullPath(token[2..]));
                continue;
            }

            if (token == "-L" && index + 1 < tokens.Count)
            {
                librarySearchDirectories.Add(Path.GetFullPath(tokens[++index]));
                continue;
            }

            if (token.StartsWith("-L", StringComparison.Ordinal) && token.Length > 2)
            {
                librarySearchDirectories.Add(Path.GetFullPath(token[2..]));
                continue;
            }

            linkArguments.Add(token);
        }

        return NativePkgConfigResolveResult.Successful(
            CombineDistinct(includeDirectories),
            CombineDistinct(librarySearchDirectories),
            CombineDistinct(linkArguments));
    }

    private static IReadOnlyList<string> SplitPkgConfigFlags(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var escaped = false;

        foreach (var ch in text)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (escaped)
        {
            current.Append('\\');
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string FormatNativeLibraryArgument(string library, LlvmTargetInfo? targetInfo)
    {
        if (library.StartsWith("-l", StringComparison.Ordinal)
            || library.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
            || library.EndsWith(".a", StringComparison.OrdinalIgnoreCase)
            || library.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || library.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
            || library.Contains(Path.DirectorySeparatorChar)
            || library.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(library))
        {
            return library;
        }

        return $"-l{library}";
    }

    private static IReadOnlyList<CompilerDiagnostic> BuildMissingNativeLibraryDiagnostics(
        NativeToolchainResult toolchainResult,
        IReadOnlyList<string> linkArguments)
    {
        var toolOutput = string.Join(
            Environment.NewLine,
            [toolchainResult.StandardOutput, toolchainResult.StandardError]);
        if (!LooksLikeMissingLibraryOutput(toolOutput))
        {
            return [];
        }

        var diagnostics = new List<CompilerDiagnostic>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateNativeLibraryLinkCandidates(linkArguments))
        {
            if (!MissingLibraryOutputMentionsCandidate(toolOutput, candidate)
                || !seen.Add(candidate.DisplayName))
            {
                continue;
            }

            diagnostics.Add(new CompilerDiagnostic(
                Code: "STK7204",
                Severity: DiagnosticSeverity.Error,
                Message:
                    $"Native library '{candidate.DisplayName}' could not be found while linking. Install that library for this target, add its directory with '-L' or '--native-library-dir', or remove the native dependency if it is not needed.",
                Stage: "native-link"));
        }

        return diagnostics;
    }

    private static bool LooksLikeMissingLibraryOutput(string toolOutput)
    {
        return toolOutput.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            || toolOutput.Contains("library not found", StringComparison.OrdinalIgnoreCase)
            || toolOutput.Contains("unable to find library", StringComparison.OrdinalIgnoreCase)
            || toolOutput.Contains("could not find", StringComparison.OrdinalIgnoreCase)
            || toolOutput.Contains("cannot open file", StringComparison.OrdinalIgnoreCase)
            || toolOutput.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MissingLibraryOutputMentionsCandidate(
        string toolOutput,
        NativeLibraryLinkCandidate candidate)
    {
        if (toolOutput.Contains(candidate.LinkArgument, StringComparison.OrdinalIgnoreCase)
            || toolOutput.Contains(candidate.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alias in candidate.Aliases)
        {
            if (toolOutput.Contains(alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<NativeLibraryLinkCandidate> EnumerateNativeLibraryLinkCandidates(
        IReadOnlyList<string> linkArguments)
    {
        foreach (var argument in linkArguments)
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            var trimmed = argument.Trim();
            if (trimmed.StartsWith("-l", StringComparison.Ordinal) && trimmed.Length > 2)
            {
                var name = trimmed[2..];
                var displayName = name.StartsWith(":", StringComparison.Ordinal) ? name[1..] : name;
                yield return new NativeLibraryLinkCandidate(
                    displayName,
                    trimmed,
                    BuildNativeLibraryAliases(displayName));
                continue;
            }

            if (IsSimpleNativeLibraryFileName(trimmed))
            {
                var displayName = Path.GetFileName(trimmed);
                yield return new NativeLibraryLinkCandidate(
                    displayName,
                    trimmed,
                    BuildNativeLibraryAliases(Path.GetFileNameWithoutExtension(displayName)));
            }
        }
    }

    private static bool IsSimpleNativeLibraryFileName(string value)
    {
        return !Path.IsPathRooted(value)
            && !value.Contains('/', StringComparison.Ordinal)
            && !value.Contains('\\', StringComparison.Ordinal)
            && (value.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".a", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> BuildNativeLibraryAliases(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return [];
        }

        var aliases = new List<string>
        {
            libraryName,
            $"-l{libraryName}",
            $"{libraryName}.lib",
            $"lib{libraryName}.a",
            $"lib{libraryName}.so",
            $"lib{libraryName}.dylib"
        };

        if (libraryName.StartsWith("lib", StringComparison.Ordinal) && libraryName.Length > 3)
        {
            aliases.Add(libraryName[3..]);
            aliases.Add($"-l{libraryName[3..]}");
        }

        return aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsWindowsTarget(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo?.Triple is { Length: > 0 } triple)
        {
            return triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("win32", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase);
        }

        return OperatingSystem.IsWindows();
    }

    private static IReadOnlyList<string> CombineDistinct(
        IEnumerable<string> first,
        IEnumerable<string>? second = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var combined = new List<string>();

        foreach (var value in first.Concat(second ?? []))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                combined.Add(trimmed);
            }
        }

        return combined;
    }

    private static DependencyCompileResult CompileDependencyObject(
        LoadedModuleDocument module,
        CompilerOptions rootOptions,
        string intermediateDirectory,
        bool preserveTemps,
        ToolchainMetrics? toolchainMetrics = null)
    {
        var dependencyResult = CompileDependencyLlvm(module, rootOptions);
        if (!dependencyResult.Success)
        {
            return new DependencyCompileResult(
                false,
                null,
                dependencyResult.Diagnostics,
                dependencyResult.Logs,
                null,
                RequiresMathLibrary: false,
                RequiresWinsockLibrary: false,
                RequiresWindowsSynchronizationLibrary: false);
        }

        var toolchainResult = EmitDependencyObject(dependencyResult, rootOptions, intermediateDirectory, preserveTemps);
        toolchainMetrics?.AddLlvmObject(toolchainResult);
        return toolchainResult.Succeeded
            ? new DependencyCompileResult(true, toolchainResult.OutputPath, [], dependencyResult.Logs, toolchainResult, dependencyResult.RequiresMathLibrary, dependencyResult.RequiresWinsockLibrary, dependencyResult.RequiresWindowsSynchronizationLibrary)
            : new DependencyCompileResult(false, null, [], dependencyResult.Logs, toolchainResult, RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false);
    }

    private static SourceDependencyLinkResult CompileAndEmitReferencedDependencyObjects(
        IReadOnlyList<LoadedModuleDocument> modules,
        string rootLlvmText,
        CompilerOptions rootOptions,
        string intermediateDirectory,
        bool preserveTemps,
        ToolchainMetrics? toolchainMetrics,
        bool enableLto)
    {
        var compiledModules = new List<DependencyLlvmCompileResult>(modules.Count);
        foreach (var module in modules)
        {
            var dependencyResult = CompileDependencyLlvm(module, rootOptions);
            if (!dependencyResult.Success)
            {
                return new SourceDependencyLinkResult(
                    false,
                    [],
                    dependencyResult.Diagnostics,
                    dependencyResult.Logs,
                    null,
                    RequiresMathLibrary: false,
                    RequiresWinsockLibrary: false,
                    RequiresWindowsSynchronizationLibrary: false);
            }

            compiledModules.Add(dependencyResult);
        }

        var rootSymbols = SummarizeLlvmSymbols(rootLlvmText);
        var unresolvedSymbols = new HashSet<string>(rootSymbols.ReferencedSymbols, StringComparer.Ordinal);
        var forceEmittedModuleIndexes = new HashSet<int>(
            compiledModules
                .Select(static (module, index) => new { Module = module, Index = index })
                .Where(static item => item.Module.LlvmText is not null && ContainsMonomorphizedStarkSymbols(item.Module.LlvmText))
                .Select(static item => item.Index));
        var emittedModuleIndexes = new HashSet<int>();
        var objectPaths = new List<string>();
        var requiresMathLibrary = false;
        var requiresWinsockLibrary = false;
        var requiresWindowsSynchronizationLibrary = false;
        var madeProgress = true;

        while (madeProgress)
        {
            madeProgress = false;
            for (var index = 0; index < compiledModules.Count; index++)
            {
                if (emittedModuleIndexes.Contains(index))
                {
                    continue;
                }

                var dependencyResult = compiledModules[index];
                if (!forceEmittedModuleIndexes.Contains(index)
                    && !dependencyResult.Symbols.DefinedSymbols.Overlaps(unresolvedSymbols))
                {
                    continue;
                }

                var toolchainResult = EmitDependencyObject(
                    dependencyResult,
                    rootOptions,
                    intermediateDirectory,
                    preserveTemps,
                    enableLto
                        && ShouldEnableDependencyLto(dependencyResult.Module)
                        && !LlvmTextReferencesSystemCollections(dependencyResult.LlvmText));
                toolchainMetrics?.AddLlvmObject(toolchainResult);
                if (!toolchainResult.Succeeded)
                {
                    return new SourceDependencyLinkResult(
                        false,
                        objectPaths,
                        [],
                        dependencyResult.Logs,
                        toolchainResult,
                        requiresMathLibrary,
                        requiresWinsockLibrary,
                        requiresWindowsSynchronizationLibrary);
                }

                emittedModuleIndexes.Add(index);
                objectPaths.Add(toolchainResult.OutputPath);
                requiresMathLibrary |= dependencyResult.RequiresMathLibrary;
                requiresWinsockLibrary |= dependencyResult.RequiresWinsockLibrary;
                requiresWindowsSynchronizationLibrary |= dependencyResult.RequiresWindowsSynchronizationLibrary;
                unresolvedSymbols.ExceptWith(dependencyResult.Symbols.DefinedSymbols);
                foreach (var referencedSymbol in dependencyResult.Symbols.ReferencedSymbols)
                {
                    if (!dependencyResult.Symbols.DefinedSymbols.Contains(referencedSymbol))
                    {
                        unresolvedSymbols.Add(referencedSymbol);
                    }
                }

                madeProgress = true;
            }
        }

        return new SourceDependencyLinkResult(
            true,
            objectPaths,
            [],
            compiledModules.SelectMany(static module => module.Logs).ToArray(),
            null,
            requiresMathLibrary,
            requiresWinsockLibrary,
            requiresWindowsSynchronizationLibrary);
    }

    private static bool ContainsMonomorphizedStarkSymbols(string llvmText)
    {
        return llvmText.Contains("__stark_mono_", StringComparison.Ordinal);
    }

    private static DependencyLlvmCompileResult CompileDependencyLlvm(
        LoadedModuleDocument module,
        CompilerOptions rootOptions)
    {
        if (rootOptions.ModuleResolver is not IModuleSourceResolver sourceResolver
            || !sourceResolver.TryLoadModuleSource(module.Reference, out var sourceText, out var sourceFilePath))
        {
            if (module.Reference.FilePath is null || !File.Exists(module.Reference.FilePath))
            {
            return new DependencyLlvmCompileResult(false, module, null, EmptyLlvmSymbolSummary(), [], [], RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false);
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
            return new DependencyLlvmCompileResult(false, module, null, EmptyLlvmSymbolSummary(), dependencyResult.Diagnostics, dependencyResult.Logs, RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false);
        }

        if (!dependencyResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            return new DependencyLlvmCompileResult(false, module, null, EmptyLlvmSymbolSummary(), [], dependencyResult.Logs, RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false);
        }

        var requiresMathLibrary = TargetRequiresExplicitMathLibrary(rootOptions.TargetInfo)
            && LlvmTextRequiresMathLibrary(llvmModule.Text);
        var requiresWinsockLibrary = TargetRequiresWinsockLibrary(rootOptions.TargetInfo)
            && LlvmTextRequiresWinsockLibrary(llvmModule.Text);
        var requiresWindowsSynchronizationLibrary = TargetRequiresWindowsSynchronizationLibrary(rootOptions.TargetInfo)
            && LlvmTextRequiresWindowsSynchronizationLibrary(llvmModule.Text);
        return new DependencyLlvmCompileResult(
            true,
            module,
            llvmModule.Text,
            SummarizeLlvmSymbols(llvmModule.Text),
            [],
            dependencyResult.Logs,
            requiresMathLibrary,
            requiresWinsockLibrary,
            requiresWindowsSynchronizationLibrary);
    }

    private static NativeToolchainResult EmitDependencyObject(
        DependencyLlvmCompileResult dependencyResult,
        CompilerOptions rootOptions,
        string intermediateDirectory,
        bool preserveTemps,
        bool enableLto = false)
    {
        if (dependencyResult.LlvmText is null)
        {
            return new NativeToolchainResult(
                false,
                string.Empty,
                string.Empty,
                "LLVM IR was not produced for dependency module.");
        }

        var module = dependencyResult.Module;
        var objectPath = Path.Combine(
            intermediateDirectory,
            $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}{(OperatingSystem.IsWindows() ? ".obj" : ".o")}");
        var llvmPath = preserveTemps
            ? Path.Combine(intermediateDirectory, $"{module.SyntaxModel.ModuleName.Replace(".", "_", StringComparison.Ordinal)}.ll")
            : null;
        return NativeToolchain.EmitObject(
            dependencyResult.LlvmText,
            objectPath,
            preservedLlvmOutputPath: llvmPath,
            targetInfo: rootOptions.TargetInfo,
            optimizationLevel: rootOptions.OptimizationLevel,
            enableLto: enableLto);
    }

    private static bool ShouldEnableExecutableLto(CompilerOptimizationLevel optimizationLevel, string? linkerTool)
    {
        if (optimizationLevel is CompilerOptimizationLevel.O0 or CompilerOptimizationLevel.Og or CompilerOptimizationLevel.O1)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(linkerTool)
            && !Path.GetFileName(linkerTool).Contains("clang", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NativeToolchain.SupportsExecutableThinLto();
    }

    internal static bool ShouldEnableDependencyLto(LoadedModuleDocument module)
    {
        if (IsBackendOpaque(module))
        {
            return false;
        }

        // System.Memory stays native for now: owned text allocation can
        // miscompile when root code, System.Text, and System.Memory all
        // participate in the same ThinLTO link.
        return !string.Equals(module.SyntaxModel.ModuleName, "System.Memory", StringComparison.Ordinal);
    }

    internal static bool ShouldEnableRootModuleLto(CompilationResult result)
    {
        return !result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
            || syntaxModel?.BackendOptimizationMode != ModuleBackendOptimizationMode.Opaque;
    }

    private static bool IsBackendOpaque(LoadedModuleDocument module)
    {
        return module.SyntaxModel.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
            || module.PackageImageFacts?.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque;
    }

    private static bool LlvmTextReferencesSystemCollections(string? llvmText)
    {
        if (string.IsNullOrWhiteSpace(llvmText))
        {
            return false;
        }

        foreach (var rawLine in llvmText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith("declare ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("@System_Collections", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsesPrecompiledStarkLibraries(CompilationResult result)
    {
        return result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
            && loadedModules is not null
            && loadedModules.ImportedModules.Any(static module =>
                !module.Reference.IsExternal
                && !string.IsNullOrWhiteSpace(module.Reference.LibraryPath));
    }

    private static LlvmSymbolSummary SummarizeLlvmSymbols(string llvmText)
    {
        var definedSymbols = new HashSet<string>(StringComparer.Ordinal);
        var referencedSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in llvmText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (IsPureLlvmDeclarationLine(rawLine))
            {
                continue;
            }

            if (TryReadDefinedLlvmSymbol(rawLine, out var definedSymbol))
            {
                definedSymbols.Add(definedSymbol);
            }

            for (var index = 0; index < rawLine.Length; index++)
            {
                if (rawLine[index] == '@' && TryReadLlvmSymbolAt(rawLine, index, out var symbol, out var endIndex))
                {
                    referencedSymbols.Add(symbol);
                    index = endIndex - 1;
                }
            }
        }

        referencedSymbols.ExceptWith(definedSymbols);
        return new LlvmSymbolSummary(definedSymbols, referencedSymbols);
    }

    private static bool IsPureLlvmDeclarationLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("declare ", StringComparison.Ordinal))
        {
            return true;
        }

        if (!trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            return false;
        }

        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex < 0)
        {
            return false;
        }

        var initializer = trimmed[(equalsIndex + 1)..].TrimStart();
        return initializer.StartsWith("external", StringComparison.Ordinal)
            || initializer.StartsWith("extern_weak", StringComparison.Ordinal);
    }

    private static LlvmSymbolSummary EmptyLlvmSymbolSummary()
    {
        return new LlvmSymbolSummary(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool TryReadDefinedLlvmSymbol(string line, out string symbol)
    {
        symbol = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("define ", StringComparison.Ordinal))
        {
            var atIndex = trimmed.IndexOf('@');
            return atIndex >= 0 && TryReadLlvmSymbolAt(trimmed, atIndex, out symbol, out _);
        }

        if (!trimmed.StartsWith("@", StringComparison.Ordinal))
        {
            return false;
        }

        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex < 0)
        {
            return false;
        }

        var initializer = trimmed[(equalsIndex + 1)..].TrimStart();
        if (initializer.StartsWith("external", StringComparison.Ordinal)
            || initializer.StartsWith("extern_weak", StringComparison.Ordinal))
        {
            return false;
        }

        return TryReadLlvmSymbolAt(trimmed, 0, out symbol, out _);
    }

    private static bool TryReadLlvmSymbolAt(string text, int atIndex, out string symbol, out int endIndex)
    {
        symbol = string.Empty;
        endIndex = atIndex;
        if (atIndex < 0 || atIndex >= text.Length || text[atIndex] != '@')
        {
            return false;
        }

        var start = atIndex + 1;
        var index = start;
        while (index < text.Length && IsLlvmSymbolCharacter(text[index]))
        {
            index++;
        }

        if (index == start)
        {
            return false;
        }

        symbol = text[start..index];
        endIndex = index;
        return true;
    }

    private static bool IsLlvmSymbolCharacter(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is '_' or '.' or '$';
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
        await stdout.WriteLineAsync("Project Commands:");
        await stdout.WriteLineAsync("  build          Build the current Stark project or solution from a manifest");
        await stdout.WriteLineAsync("  run            Build and run the current Stark project or solution target");
        await stdout.WriteLineAsync("  test           Run tests for the current Stark project or solution");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Workflows:");
        await stdout.WriteLineAsync("  (default)      Run the full compilation pipeline and print a pass summary");
        await stdout.WriteLineAsync("  --check       Validate through ownership/lifetime analysis");
        await stdout.WriteLineAsync("  --emit-mir    Print lowered MIR");
        await stdout.WriteLineAsync("  --emit-ssa    Print lowered SSA");
        await stdout.WriteLineAsync("  --emit-llvm   Print emitted LLVM IR");
        await stdout.WriteLineAsync("  --emit-obj    Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --compile-only Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --emit-lib    Build a static library and Stark package image");
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
        await stdout.WriteLineAsync("  -O0|-Og|-O1|-O2|-O3            Select the optimization level for frontend/codegen behavior (default: -O3)");
        await stdout.WriteLineAsync("  --optimize <0|g|1|2|3>         Long-form optimization level control");
        await stdout.WriteLineAsync("  --linker <tool>                Override the executable linker tool");
        await stdout.WriteLineAsync("  --archiver <tool>              Override the static library archiver tool");
        await stdout.WriteLineAsync("  --link-arg <arg>               Pass an additional argument through to the linker");
        await stdout.WriteLineAsync("  --native-source <path>         Add a package-owned native source file");
        await stdout.WriteLineAsync("  --native-include-dir <dir>     Add a package-owned native include directory");
        await stdout.WriteLineAsync("  --native-library-dir <dir>     Add a package-owned native library search directory");
        await stdout.WriteLineAsync("  --native-library <name>        Add a package-owned native library");
        await stdout.WriteLineAsync("  --native-pkg-config <name>     Add a package-owned pkg-config discovery package");
        await stdout.WriteLineAsync("  --native-link-arg <arg>        Add a package-owned native linker argument");
        await stdout.WriteLineAsync("  --save-temps <dir>             Preserve intermediate LLVM and object files in <dir>");
        await stdout.WriteLineAsync("  --toolchain-metrics <path>     Write native LLVM/link timing metrics as key=value lines");
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
            CliMode.EmitSsa => "prune-branches",
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
            case "G":
            case "OG":
                optimizationLevel = CompilerOptimizationLevel.Og;
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
        bool RequiresMathLibrary,
        bool RequiresWinsockLibrary,
        bool RequiresWindowsSynchronizationLibrary);

    private sealed record DependencyLlvmCompileResult(
        bool Success,
        LoadedModuleDocument Module,
        string? LlvmText,
        LlvmSymbolSummary Symbols,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        IReadOnlyList<CompilerLogEntry> Logs,
        bool RequiresMathLibrary,
        bool RequiresWinsockLibrary,
        bool RequiresWindowsSynchronizationLibrary);

    private sealed record SourceDependencyLinkResult(
        bool Success,
        IReadOnlyList<string> ObjectPaths,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        IReadOnlyList<CompilerLogEntry> Logs,
        NativeToolchainResult? ToolchainResult,
        bool RequiresMathLibrary,
        bool RequiresWinsockLibrary,
        bool RequiresWindowsSynchronizationLibrary);

    private sealed record LlvmSymbolSummary(
        HashSet<string> DefinedSymbols,
        HashSet<string> ReferencedSymbols);

    private sealed record NativeDependencySet(
        string PackageName,
        string BaseDirectory,
        string? ManifestPath,
        StarkPackageNativeDependencyManifest Dependencies);

    private sealed record NativeDependencyLinkResult(
        bool Success,
        IReadOnlyList<string> ObjectPaths,
        IReadOnlyList<string> LibrarySearchDirectories,
        IReadOnlyList<string> LinkArguments,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        NativeToolchainResult? ToolchainResult);

    private sealed record NativePkgConfigResolveResult(
        bool Success,
        IReadOnlyList<string> IncludeDirectories,
        IReadOnlyList<string> LibrarySearchDirectories,
        IReadOnlyList<string> LinkArguments,
        IReadOnlyList<CompilerDiagnostic> Diagnostics)
    {
        public static NativePkgConfigResolveResult Successful(
            IReadOnlyList<string> includeDirectories,
            IReadOnlyList<string> librarySearchDirectories,
            IReadOnlyList<string> linkArguments)
            => new(true, includeDirectories, librarySearchDirectories, linkArguments, []);

        public static NativePkgConfigResolveResult Failed(CompilerDiagnostic diagnostic)
            => new(false, [], [], [], [diagnostic]);
    }

    private sealed record NativeLibraryLinkCandidate(
        string DisplayName,
        string LinkArgument,
        IReadOnlyList<string> Aliases);

    private sealed record NativeDependencyCliOptions(
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> IncludeDirectories,
        IReadOnlyList<string> LibraryDirectories,
        IReadOnlyList<string> Libraries,
        IReadOnlyList<string> PkgConfigPackages,
        IReadOnlyList<string> LinkArguments)
    {
        public bool HasAny =>
            Sources.Count != 0
            || IncludeDirectories.Count != 0
            || LibraryDirectories.Count != 0
            || Libraries.Count != 0
            || PkgConfigPackages.Count != 0
            || LinkArguments.Count != 0;

        public StarkPackageNativeDependencyManifest? ToManifest(string packageImageDirectory)
        {
            if (!HasAny)
            {
                return null;
            }

            return new StarkPackageNativeDependencyManifest(
                Sources: NormalizeSourcePathList(Sources, packageImageDirectory),
                IncludeDirectories: NormalizePathList(IncludeDirectories, packageImageDirectory),
                LibraryDirectories: NormalizePathList(LibraryDirectories, packageImageDirectory),
                Libraries: NormalizeTextList(Libraries),
                LinkArguments: NormalizeTextList(LinkArguments),
                PkgConfigPackages: NormalizeTextList(PkgConfigPackages));
        }

        private static IReadOnlyList<string>? NormalizeSourcePathList(IReadOnlyList<string> values, string packageImageDirectory)
        {
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var trimmed = value.Trim();
                var fullPath = Path.GetFullPath(trimmed);
                var relativePath = Path.GetRelativePath(packageImageDirectory, fullPath);
                var manifestValue = Path.IsPathRooted(relativePath)
                    ? fullPath
                    : relativePath;
                if (seen.Add(manifestValue))
                {
                    normalized.Add(manifestValue);
                }
            }

            return normalized.Count == 0 ? null : normalized;
        }

        private static IReadOnlyList<string>? NormalizePathList(IReadOnlyList<string> values, string packageImageDirectory)
        {
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var trimmed = value.Trim();
                var fullPath = Path.GetFullPath(trimmed);
                var relativePath = Path.GetRelativePath(packageImageDirectory, fullPath);
                var manifestValue = !relativePath.StartsWith("..", StringComparison.Ordinal)
                    && !Path.IsPathRooted(relativePath)
                        ? relativePath
                        : Path.IsPathRooted(trimmed)
                            ? fullPath
                            : relativePath;
                if (seen.Add(manifestValue))
                {
                    normalized.Add(manifestValue);
                }
            }

            return normalized.Count == 0 ? null : normalized;
        }

        private static IReadOnlyList<string>? NormalizeTextList(IReadOnlyList<string> values)
        {
            var normalized = values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return normalized.Length == 0 ? null : normalized;
        }
    }

    private sealed record ToolchainCliOptions(
        string? LinkerTool,
        string? ArchiverTool,
        IReadOnlyList<string> LibrarySearchDirectories,
        IReadOnlyList<string> LinkArguments,
        string? SaveTempsDirectory,
        string? ToolchainMetricsPath,
        NativeDependencyCliOptions NativeDependencies);

    private sealed class ToolchainMetrics
    {
        public long LlvmObjectMicroseconds { get; private set; }

        public int LlvmObjectCount { get; private set; }

        public long NativeObjectMicroseconds { get; private set; }

        public int NativeObjectCount { get; private set; }

        public long LinkMicroseconds { get; private set; }

        public int LinkCount { get; private set; }

        public long ArchiveMicroseconds { get; private set; }

        public int ArchiveCount { get; private set; }

        public void AddLlvmObject(NativeToolchainResult result)
        {
            LlvmObjectMicroseconds += ToMicroseconds(result.Duration);
            LlvmObjectCount++;
        }

        public void AddNativeObject(NativeToolchainResult result)
        {
            NativeObjectMicroseconds += ToMicroseconds(result.Duration);
            NativeObjectCount++;
        }

        public void AddLink(NativeToolchainResult result)
        {
            LinkMicroseconds += ToMicroseconds(result.Duration);
            LinkCount++;
        }

        public void AddArchive(NativeToolchainResult result)
        {
            ArchiveMicroseconds += ToMicroseconds(result.Duration);
            ArchiveCount++;
        }

        public async Task WriteAsync(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory);
            await File.WriteAllTextAsync(
                fullPath,
                string.Join(
                    Environment.NewLine,
                    [
                        "timing_unit=microseconds",
                        $"llvm_object_us={LlvmObjectMicroseconds}",
                        $"llvm_object_count={LlvmObjectCount}",
                        $"native_object_us={NativeObjectMicroseconds}",
                        $"native_object_count={NativeObjectCount}",
                        $"link_us={LinkMicroseconds}",
                        $"link_count={LinkCount}",
                        $"archive_us={ArchiveMicroseconds}",
                        $"archive_count={ArchiveCount}",
                        $"toolchain_us={LlvmObjectMicroseconds + NativeObjectMicroseconds + LinkMicroseconds + ArchiveMicroseconds}"
                    ])
                + Environment.NewLine);
        }

        private static long ToMicroseconds(TimeSpan duration)
        {
            return (duration.Ticks * 1_000_000L + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond;
        }
    }

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
