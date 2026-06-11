using System.Text;
using System.Text.Json;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed record ModuleOptimizationSafetyFacts(
    string ModuleName,
    bool CanEmitThinLtoBitcode,
    bool CanRunNormalLlvmPasses,
    bool ContainsKnownFragileConstructs,
    bool ExposesHotInlineCandidates,
    string DecisionReason);

internal static class CompilerCli
{
    private const string Usage = "Usage: compiler [path-to-stark-file] [--check|--emit-mir|--emit-ssa|--emit-llvm|--emit-obj|--compile-only|--emit-lib|--emit-exe|--link-only|--emit-pkg|--emit-package|--inspect-pkg|--inspect-package|--host-test-inspect|--host-test-server] [-I dir|--search-dir dir]* [--no-stark-path] [-L dir|--library-dir dir]* [--link-arg arg]* [--native-source path]* [--native-include-dir dir]* [--native-library-dir dir]* [--native-library name]* [--native-pkg-config name]* [--native-link-arg arg]* [--package-library-file name] [--package-image-output path] [--package-image-json] [-o output] [--target triple] [--target-data-layout layout] [--target-cpu cpu] [--target-feature feature]* [--relocation-model mode] [--code-model model] [--strict-integer-ranges] [--linker tool] [--archiver tool] [--save-temps dir] [--toolchain-metrics path] [--diagnostic-format format] [--log-level level] [--log-verbosity mode] [--log-category name]* [--log-stage pass]* [--log-kind kind]*";
    private const int DiagnosticTabWidth = 4;
    private static readonly IReadOnlySet<string> EmptyImportedInlineCloneSeedFunctions = new HashSet<string>(StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 0 && HostCompilerTestRunner.IsCommand(args[0]))
        {
            return await HostCompilerTestRunner.RunAsync(args, stdin, stdout, stderr);
        }

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
        string? linkerTool = null;
        string? archiverTool = null;
        string? saveTempsDirectory = null;
        string? toolchainMetricsPath = null;
        string? packageLibraryFile = null;
        string? packageImageOutputPath = null;
        var emitPackageImageJson = false;
        var diagnosticFormat = DiagnosticOutputFormat.Text;
        var logLevel = DiagnosticSeverity.Warning;
        var logVerbosity = CompilerLogVerbosity.Normal;
        var logCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logKinds = new HashSet<CompilerLogKind>();
        var strictIntegerRanges = true;
        var useStarkPathEnvironment = true;
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

            if (string.Equals(argument, "--strict-integer-ranges", StringComparison.Ordinal))
            {
                strictIntegerRanges = true;
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

            if (TryReadOptionValue(argument, "--package-image-output", args, ref index, out var packageImageOutputValue))
            {
                if (string.IsNullOrWhiteSpace(packageImageOutputValue))
                {
                    await stderr.WriteLineAsync("Package image output path must not be empty.");
                    await stderr.WriteLineAsync(Usage);
                    return 1;
                }

                packageImageOutputPath = packageImageOutputValue.Trim();
                continue;
            }

            if (string.Equals(argument, "--package-image-json", StringComparison.Ordinal))
            {
                emitPackageImageJson = true;
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

            if (string.Equals(argument, "--no-stark-path", StringComparison.Ordinal))
            {
                useStarkPathEnvironment = false;
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
            if (packageImageOutputPath is not null)
            {
                await stderr.WriteLineAsync("--package-image-output is only valid for library emission.");
                await stderr.WriteLineAsync(Usage);
                return 1;
            }

            return await InspectPackageImageAsync(inputPath, outputPath, stdin, stdout, stderr, diagnosticFormat);
        }

        var requiresTargetInfo = mode is CliMode.Default
            or CliMode.EmitLlvmIr
            or CliMode.EmitObject
            or CliMode.EmitLibrary
            or CliMode.EmitExecutable;
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
        var effectiveMode = mode == CliMode.Default
            ? InferDefaultBuildMode(source, targetInfo)
            : mode;

        if (packageImageOutputPath is not null && effectiveMode != CliMode.EmitLibrary)
        {
            await stderr.WriteLineAsync("--package-image-output is only valid for library emission.");
            await stderr.WriteLineAsync(Usage);
            return 1;
        }

        var moduleResolver = ResolveModuleResolver(inputPath, searchDirectories, targetInfo, useStarkPathEnvironment);
        var pipeline = DefaultCompilerPipeline.Create();
        var compilerOptions = new CompilerOptions(
            EmitLlvmIr: effectiveMode is CliMode.EmitLlvmIr or CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable,
            TargetInfo: targetInfo,
            StopAfterPassId: ResolveStopAfterPassId(effectiveMode),
            ModuleResolver: moduleResolver,
            QualifyModuleSymbols: effectiveMode == CliMode.EmitLibrary,
            InternalizeModulePrivate: effectiveMode == CliMode.EmitExecutable,
            EnforceIntegerRangeStorageRules: strictIntegerRanges,
            // One invocation compiles the root plus each source-dependency module; parsed
            // source modules are shared across those sequential pipeline runs.
            SharedSourceModuleParseCache: new SharedSourceModuleParseCache(),
            // Binary outputs only emit owned definitions plus referenced clones, so lowered
            // functions outside that reachable set are dead weight. Inspection modes
            // (--check/--emit-mir/--emit-ssa/--emit-llvm) keep the full lowered view.
            PruneUnusedLoweredFunctions: effectiveMode is CliMode.EmitObject or CliMode.EmitLibrary or CliMode.EmitExecutable);
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

        switch (effectiveMode)
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
                return await EmitLibraryAsync(outputPath, packageImageOutputPath, inputPath, stdout, stderr, result, compilerOptions, toolchainOptions, diagnosticFormat, emitPackageImageJson);
            case CliMode.EmitExecutable:
                return await EmitExecutableAsync(outputPath, inputPath, stdout, stderr, result, compilerOptions, toolchainOptions, diagnosticFormat);
            case CliMode.EmitPackage:
                return await EmitPackageImageAsync(outputPath, inputPath, stdout, stderr, result, packageLibraryFile, toolchainOptions.NativeDependencies, diagnosticFormat, emitPackageImageJson);
            default:
                throw new InvalidOperationException($"Unhandled compiler mode '{effectiveMode}'.");
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

        var resolvedOutputPath = outputPath ?? DeriveExecutableOutputPath(inputPath, result, compilerOptions.TargetInfo);
        var linkInputs = new List<string>();
        var linkedLibraries = new HashSet<string>(StringComparer.Ordinal);
        var intermediateDirectory = CreateIntermediateDirectory(toolchainOptions.SaveTempsDirectory, "stark-link-", out var cleanupDirectory);
        var canUseExecutableLto = ShouldEnableExecutableLto(toolchainOptions.LinkerTool);
        var enableRootModuleLto = canUseExecutableLto
                                  && ShouldEnableRootModuleLto(result);
        var enableDependencyLto = canUseExecutableLto;
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
                enableLto: enableRootModuleLto);
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
            var requiresNtDllLibrary = TargetRequiresNtDllLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresNtDllLibrary(llvmModule.Text);

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

                var importedInlineCloneSeedsByModule = BuildImportedInlineCloneSeedsByModule(result);
                var sourceDependencyResult = CompileAndEmitReferencedDependencyObjects(
                    sourceDependencyModules,
                    llvmModule.Text,
                    compilerOptions,
                    intermediateDirectory,
                    preserveTemps: toolchainOptions.SaveTempsDirectory is not null,
                    toolchainMetrics: toolchainMetrics,
                    enableLto: enableDependencyLto,
                    importedInlineCloneSeedsByModule);
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
                requiresNtDllLibrary |= sourceDependencyResult.RequiresNtDllLibrary;
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
                requiresNtDllLibrary,
                compilerOptions.TargetInfo);
            var toolchainResult = NativeToolchain.LinkExecutable(
                linkInputs,
                resolvedOutputPath,
                toolchainOptions.LinkerTool,
                combinedLibrarySearchDirectories,
                linkArguments,
                compilerOptions.TargetInfo,
                enableRootModuleLto || enableDependencyLto);
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
        string? packageImageOutputPath,
        string? inputPath,
        TextWriter stdout,
        TextWriter stderr,
        CompilationResult result,
        CompilerOptions compilerOptions,
        ToolchainCliOptions toolchainOptions,
        DiagnosticOutputFormat diagnosticFormat,
        bool emitPackageImageJson)
    {
        if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            await stderr.WriteLineAsync("LLVM IR was not produced.");
            return 1;
        }

        var resolvedOutputPath = outputPath ?? DeriveLibraryOutputPath(inputPath, result);
        var objectPaths = new List<string>();
        var intermediateDirectory = CreateIntermediateDirectory(toolchainOptions.SaveTempsDirectory, "stark-lib-", out var cleanupDirectory);
        var canUseLibraryLto = ShouldEnableLibraryLto();
        var enableRootModuleLto = canUseLibraryLto
                                  && ShouldEnableRootModuleLto(result);
        var enableDependencyLto = canUseLibraryLto;
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
                enableLto: enableRootModuleLto);
            toolchainMetrics.AddLlvmObject(rootObjectResult);
            if (!rootObjectResult.Succeeded)
            {
                await WriteToolchainFailureAsync(stdout, stderr, rootObjectResult);
                return 1;
            }

            objectPaths.Add(rootObjectResult.OutputPath);
            var requiresMathLibrary = TargetRequiresExplicitMathLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresMathLibrary(llvmModule.Text);
            var requiresWinsockLibrary = TargetRequiresWinsockLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresWinsockLibrary(llvmModule.Text);
            var requiresWindowsSynchronizationLibrary = TargetRequiresWindowsSynchronizationLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresWindowsSynchronizationLibrary(llvmModule.Text);
            var requiresNtDllLibrary = TargetRequiresNtDllLibrary(compilerOptions.TargetInfo)
                && LlvmTextRequiresNtDllLibrary(llvmModule.Text);

            if (result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
                && loadedModules is not null)
            {
                var dependencyModules = loadedModules.ImportedModules
                    .Where(static module => !module.Reference.IsExternal)
                    .ToArray();

                // Optimization decisions write to the metrics stream, so they resolve
                // sequentially in module order before the compiles fan out. Every archive
                // member is needed, so all modules compile in parallel; the runs only
                // share the module resolver and the parse cache, which are thread-safe.
                var dependencyLtoDecisions = new bool[dependencyModules.Length];
                for (var index = 0; index < dependencyModules.Length; index++)
                {
                    dependencyLtoDecisions[index] = AddOptimizationDecision(
                        toolchainMetrics,
                        "library_dependency",
                        AnalyzeModuleOptimizationSafety(dependencyModules[index], enableDependencyLto)).CanEmitThinLtoBitcode;
                }

                var dependencyResults = new DependencyCompileResult[dependencyModules.Length];
                Parallel.For(
                    0,
                    dependencyModules.Length,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    index => dependencyResults[index] = CompileDependencyObject(
                        dependencyModules[index],
                        compilerOptions,
                        intermediateDirectory,
                        preserveTemps: toolchainOptions.SaveTempsDirectory is not null,
                        toolchainMetrics: null,
                        enableLto: dependencyLtoDecisions[index]));

                foreach (var dependencyResult in dependencyResults)
                {
                    if (dependencyResult.ToolchainResult is not null)
                    {
                        toolchainMetrics.AddLlvmObject(dependencyResult.ToolchainResult);
                    }

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

                    requiresMathLibrary |= dependencyResult.RequiresMathLibrary;
                    requiresWinsockLibrary |= dependencyResult.RequiresWinsockLibrary;
                    requiresWindowsSynchronizationLibrary |= dependencyResult.RequiresWindowsSynchronizationLibrary;
                    requiresNtDllLibrary |= dependencyResult.RequiresNtDllLibrary;
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

            var manifestPath = Path.GetFullPath(PackageImageBinaryFormat.NormalizeBinaryImagePath(
                packageImageOutputPath ?? DeriveLibraryManifestPath(toolchainResult.OutputPath, inputPath, result)));
            var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(manifestDirectory);
            var manifest = PackageImageBuilder.Create(
                result,
                toolchainResult.OutputPath,
                BuildPackageNativeDependencyManifest(
                    toolchainOptions.NativeDependencies,
                    manifestDirectory,
                    requiresMathLibrary,
                    requiresWinsockLibrary,
                    requiresWindowsSynchronizationLibrary,
                    requiresNtDllLibrary))
                with
                {
                    LibraryFileName = BuildPackageLibraryReference(toolchainResult.OutputPath, manifestPath)
                };
            await File.WriteAllBytesAsync(manifestPath, PackageImageBinaryFormat.Encode(manifest));

            await toolchainMetrics.WriteAsync(toolchainOptions.ToolchainMetricsPath);
            await stdout.WriteLineAsync($"Emitted static library: {toolchainResult.OutputPath}");
            await stdout.WriteLineAsync($"Emitted package image: {manifestPath}");
            if (emitPackageImageJson)
            {
                var jsonPath = PackageImageBinaryFormat.JsonSidecarPath(manifestPath);
                await File.WriteAllTextAsync(jsonPath, manifest.ToJson());
                await stdout.WriteLineAsync($"Emitted package image JSON: {jsonPath}");
            }

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
            targetInfo: compilerOptions.TargetInfo);
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
        DiagnosticOutputFormat diagnosticFormat,
        bool emitPackageImageJson)
    {
        var packageLibraryFileName = ResolvePackageLibraryFileName(packageLibraryFile, inputPath, result);
        var resolvedOutputPath = Path.GetFullPath(PackageImageBinaryFormat.NormalizeBinaryImagePath(
            outputPath ?? DerivePackageImageOutputPath(inputPath, result, packageLibraryFileName)));
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

        await File.WriteAllBytesAsync(resolvedOutputPath, PackageImageBinaryFormat.Encode(packageImage));
        await stdout.WriteLineAsync($"Emitted package image: {resolvedOutputPath}");
        await stdout.WriteLineAsync($"Package library file: {packageImage.LibraryFileName}");
        if (emitPackageImageJson)
        {
            var jsonPath = PackageImageBinaryFormat.JsonSidecarPath(resolvedOutputPath);
            await File.WriteAllTextAsync(jsonPath, packageImage.ToJson());
            await stdout.WriteLineAsync($"Emitted package image JSON: {jsonPath}");
        }

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

            var bytes = await File.ReadAllBytesAsync(fullPath);
            if (PackageImageBinaryFormat.HasBinaryMagic(bytes))
            {
                if (!PackageImageBinaryFormat.TryDecode(bytes, out var binaryManifest))
                {
                    await stderr.WriteLineAsync($"Package image file '{fullPath}' is not a readable binary package image.");
                    return 1;
                }

                json = binaryManifest.ToJson();
            }
            else
            {
                json = System.Text.Encoding.UTF8.GetString(bytes);
            }

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

    private static IModuleResolver? ResolveModuleResolver(
        string? inputPath,
        IReadOnlyList<string> searchDirectories,
        LlvmTargetInfo? targetInfo,
        bool useStarkPathEnvironment)
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

        var environmentSearchPath = useStarkPathEnvironment
            ? Environment.GetEnvironmentVariable("STARK_PATH")
            : null;
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
        bool requiresNtDllLibrary,
        LlvmTargetInfo? targetInfo)
    {
        if ((!requiresMathLibrary || explicitArguments.Contains("-lm", StringComparer.Ordinal))
            && (!requiresWinsockLibrary || ContainsWinsockLinkArgument(explicitArguments))
            && (!requiresWindowsSynchronizationLibrary || ContainsWindowsSynchronizationLinkArgument(explicitArguments))
            && (!requiresNtDllLibrary || ContainsNtDllLinkArgument(explicitArguments)))
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

        if (requiresNtDllLibrary && !ContainsNtDllLinkArgument(explicitArguments))
        {
            combined.Add(NtDllLinkArgument(targetInfo));
        }

        return combined;
    }

    private static StarkPackageNativeDependencyManifest? BuildPackageNativeDependencyManifest(
        NativeDependencyCliOptions nativeDependencies,
        string packageImageDirectory,
        bool requiresMathLibrary,
        bool requiresWinsockLibrary,
        bool requiresWindowsSynchronizationLibrary,
        bool requiresNtDllLibrary)
    {
        var manifest = nativeDependencies.ToManifest(packageImageDirectory);
        if (!requiresMathLibrary
            && !requiresWinsockLibrary
            && !requiresWindowsSynchronizationLibrary
            && !requiresNtDllLibrary)
        {
            return manifest;
        }

        var libraries = manifest?.Libraries?.ToList() ?? [];
        var linkArguments = manifest?.LinkArguments ?? [];

        if (requiresMathLibrary
            && !ContainsNativeLibraryName(libraries, "m")
            && !linkArguments.Contains("-lm", StringComparer.Ordinal))
        {
            libraries.Add("m");
        }

        if (requiresWinsockLibrary
            && !ContainsNativeLibraryName(libraries, "ws2_32")
            && !ContainsWinsockLinkArgument(linkArguments))
        {
            libraries.Add("ws2_32");
        }

        if (requiresWindowsSynchronizationLibrary
            && !ContainsNativeLibraryName(libraries, "synchronization")
            && !ContainsWindowsSynchronizationLinkArgument(linkArguments))
        {
            libraries.Add("synchronization");
        }

        if (requiresNtDllLibrary
            && !ContainsNativeLibraryName(libraries, "ntdll")
            && !ContainsNtDllLinkArgument(linkArguments))
        {
            libraries.Add("ntdll");
        }

        if (manifest is null && libraries.Count == 0)
        {
            return null;
        }

        return new StarkPackageNativeDependencyManifest(
            Sources: manifest?.Sources,
            IncludeDirectories: manifest?.IncludeDirectories,
            LibraryDirectories: manifest?.LibraryDirectories,
            Libraries: libraries.Count == 0 ? null : CombineDistinct(libraries),
            LinkArguments: manifest?.LinkArguments,
            PkgConfigPackages: manifest?.PkgConfigPackages);
    }

    private static bool ContainsNativeLibraryName(IReadOnlyList<string> libraries, string name)
    {
        foreach (var library in libraries)
        {
            var trimmed = library.Trim();
            if (trimmed.StartsWith("-l", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }

            if (trimmed.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(".a", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = Path.GetFileNameWithoutExtension(trimmed);
            }

            if (string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static bool TargetRequiresNtDllLibrary(LlvmTargetInfo? targetInfo)
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

    private static string NtDllLinkArgument(LlvmTargetInfo? targetInfo)
    {
        _ = targetInfo;
        return "-lntdll";
    }

    private static bool ContainsNtDllLinkArgument(IReadOnlyList<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "ntdll.lib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "-lntdll", StringComparison.OrdinalIgnoreCase))
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

    private static bool LlvmTextRequiresNtDllLibrary(string llvmText)
    {
        ReadOnlySpan<string> symbolNames =
        [
            "@NtReadFile(",
            "@NtWriteFile("
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
                    compilerOptions.TargetInfo);
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
        ToolchainMetrics? toolchainMetrics = null,
        bool enableLto = false)
    {
        var dependencyResult = CompileDependencyLlvm(module, rootOptions, importedInlineCloneSeedFunctions: null);
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
                RequiresWindowsSynchronizationLibrary: false,
                RequiresNtDllLibrary: false);
        }

        var toolchainResult = EmitDependencyObject(dependencyResult, rootOptions, intermediateDirectory, preserveTemps, enableLto);
        toolchainMetrics?.AddLlvmObject(toolchainResult);
        return toolchainResult.Succeeded
            ? new DependencyCompileResult(true, toolchainResult.OutputPath, [], dependencyResult.Logs, toolchainResult, dependencyResult.RequiresMathLibrary, dependencyResult.RequiresWinsockLibrary, dependencyResult.RequiresWindowsSynchronizationLibrary, dependencyResult.RequiresNtDllLibrary)
            : new DependencyCompileResult(false, null, [], dependencyResult.Logs, toolchainResult, RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false, RequiresNtDllLibrary: false);
    }

    private static SourceDependencyLinkResult CompileAndEmitReferencedDependencyObjects(
        IReadOnlyList<LoadedModuleDocument> modules,
        string rootLlvmText,
        CompilerOptions rootOptions,
        string intermediateDirectory,
        bool preserveTemps,
        ToolchainMetrics? toolchainMetrics,
        bool enableLto,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? importedInlineCloneSeedsByModule)
    {
        var rootSymbols = SummarizeLlvmSymbols(rootLlvmText);
        var unresolvedSymbols = new HashSet<string>(rootSymbols.ReferencedSymbols, StringComparer.Ordinal);
        var emittedModuleIndexes = new HashSet<int>();
        var broadenedModuleIndexes = new HashSet<int>();
        var objectPaths = new List<string>();
        var requiresMathLibrary = false;
        var requiresWinsockLibrary = false;
        var requiresWindowsSynchronizationLibrary = false;
        var requiresNtDllLibrary = false;

        // Dependency modules compile lazily: a module's pipeline only runs once the
        // unresolved-symbol loop suspects it (by sanitized name match), with one
        // completeness fallback wave that compiles everything left when suspicion
        // misses. Fully inlined roots therefore skip dependency compilation entirely.
        // Each wave compiles its modules in parallel; the runs only share the module
        // resolver and the parse cache, which are thread-safe.
        var compiledModules = new DependencyLlvmCompileResult?[modules.Count];

        SourceDependencyLinkResult? CompileWave(IReadOnlyList<int> indexes)
        {
            Parallel.For(
                0,
                indexes.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                position =>
                {
                    var index = indexes[position];
                    var module = modules[index];
                    compiledModules[index] = CompileDependencyLlvm(
                        module,
                        rootOptions,
                        ResolveImportedInlineCloneSeedFunctions(module, importedInlineCloneSeedsByModule));
                });

            foreach (var index in indexes)
            {
                var dependencyResult = compiledModules[index]!;
                if (!dependencyResult.Success)
                {
                    return new SourceDependencyLinkResult(
                        false,
                        objectPaths,
                        dependencyResult.Diagnostics,
                        dependencyResult.Logs,
                        null,
                        requiresMathLibrary,
                        requiresWinsockLibrary,
                        requiresWindowsSynchronizationLibrary,
                        requiresNtDllLibrary);
                }
            }

            return null;
        }

        List<int> CollectSuspectedUncompiledIndexes()
        {
            var indexes = new List<int>();
            for (var index = 0; index < modules.Count; index++)
            {
                if (compiledModules[index] is null
                    && ModuleMayDefineUnresolvedSymbol(unresolvedSymbols, modules[index]))
                {
                    indexes.Add(index);
                }
            }

            return indexes;
        }

        while (true)
        {
            var madeProgress = false;
            for (var index = 0; index < compiledModules.Length; index++)
            {
                if (emittedModuleIndexes.Contains(index)
                    || compiledModules[index] is not { } dependencyResult)
                {
                    continue;
                }

                if (!dependencyResult.Symbols.DefinedSymbols.Overlaps(unresolvedSymbols))
                {
                    continue;
                }

                var toolchainResult = EmitDependencyObject(
                    dependencyResult,
                    rootOptions,
                    intermediateDirectory,
                    preserveTemps,
                    AddOptimizationDecision(
                        toolchainMetrics,
                        "source_dependency",
                        AnalyzeModuleOptimizationSafety(dependencyResult.Module, enableLto)).CanEmitThinLtoBitcode);
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
                        requiresWindowsSynchronizationLibrary,
                        requiresNtDllLibrary);
                }

                emittedModuleIndexes.Add(index);
                objectPaths.Add(toolchainResult.OutputPath);
                requiresMathLibrary |= dependencyResult.RequiresMathLibrary;
                requiresWinsockLibrary |= dependencyResult.RequiresWinsockLibrary;
                requiresWindowsSynchronizationLibrary |= dependencyResult.RequiresWindowsSynchronizationLibrary;
                requiresNtDllLibrary |= dependencyResult.RequiresNtDllLibrary;
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

            if (madeProgress)
            {
                continue;
            }

            if (unresolvedSymbols.Count != 0)
            {
                var suspectedIndexes = CollectSuspectedUncompiledIndexes();
                if (suspectedIndexes.Count != 0)
                {
                    if (CompileWave(suspectedIndexes) is { } waveFailure)
                    {
                        return waveFailure;
                    }

                    continue;
                }
            }

            var broadenedDependency = false;
            for (var index = 0; index < compiledModules.Length; index++)
            {
                if (emittedModuleIndexes.Contains(index)
                    || broadenedModuleIndexes.Contains(index)
                    || compiledModules[index] is not { } filteredResult
                    || !filteredResult.UsesFilteredOwnedFunctionEmission
                    || !UnresolvedSymbolsMayBelongToModule(unresolvedSymbols, filteredResult.Module))
                {
                    continue;
                }

                var dependencyResult = CompileDependencyLlvm(
                    filteredResult.Module,
                    rootOptions,
                    importedInlineCloneSeedFunctions: null);
                if (!dependencyResult.Success)
                {
                    return new SourceDependencyLinkResult(
                        false,
                        objectPaths,
                        dependencyResult.Diagnostics,
                        dependencyResult.Logs,
                        null,
                        requiresMathLibrary,
                        requiresWinsockLibrary,
                        requiresWindowsSynchronizationLibrary,
                        requiresNtDllLibrary);
                }

                compiledModules[index] = dependencyResult;
                broadenedModuleIndexes.Add(index);
                broadenedDependency = true;
                break;
            }

            if (!broadenedDependency)
            {
                break;
            }
        }

        return new SourceDependencyLinkResult(
            true,
            objectPaths,
            [],
            compiledModules
                .Where(static module => module is not null)
                .SelectMany(static module => module!.Logs)
                .ToArray(),
            null,
            requiresMathLibrary,
            requiresWinsockLibrary,
            requiresWindowsSynchronizationLibrary,
            requiresNtDllLibrary);
    }

    private static bool ModuleMayDefineUnresolvedSymbol(
        IReadOnlySet<string> unresolvedSymbols,
        LoadedModuleDocument module)
    {
        if (UnresolvedSymbolsMayBelongToModule(unresolvedSymbols, module))
        {
            return true;
        }

        // Asm functions and `export` functions define binary symbols spelled exactly as
        // declared, without the module-qualified shape the name heuristic looks for.
        foreach (var declaration in module.SyntaxModel.Declarations)
        {
            if (declaration.Kind != DeclarationKind.Function || declaration.Function is null)
            {
                continue;
            }

            if ((declaration.Function.Asm is not null || declaration.Visibility == StarkVisibility.Export)
                && unresolvedSymbols.Contains(declaration.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UnresolvedSymbolsMayBelongToModule(
        IReadOnlySet<string> unresolvedSymbols,
        LoadedModuleDocument module)
    {
        var moduleName = module.SyntaxModel.ModuleName;
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        var sanitizedModuleName = SanitizeSymbolComponentForMatch(moduleName);
        foreach (var symbol in unresolvedSymbols)
        {
            if (symbol.StartsWith($"{moduleName}.", StringComparison.Ordinal)
                || symbol.StartsWith($"{sanitizedModuleName}_", StringComparison.Ordinal)
                || symbol.Contains($"_{sanitizedModuleName}_", StringComparison.Ordinal)
                || symbol.Contains($"_{sanitizedModuleName}__", StringComparison.Ordinal)
                || symbol.Contains($"__{sanitizedModuleName}__", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeSymbolComponentForMatch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "_";
        }

        var builder = new StringBuilder(text.Length);
        var previousWasUnderscore = false;
        foreach (var ch in text)
        {
            var normalized = char.IsLetterOrDigit(ch) ? ch : '_';
            if (normalized == '_')
            {
                if (previousWasUnderscore)
                {
                    continue;
                }

                previousWasUnderscore = true;
                builder.Append('_');
                continue;
            }

            previousWasUnderscore = false;
            builder.Append(normalized);
        }

        return builder.ToString().Trim('_');
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>>? BuildImportedInlineCloneSeedsByModule(
        CompilationResult rootResult)
    {
        if (!rootResult.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
            || syntaxModel is null
            || !rootResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
            || loadedModules is null
            || !rootResult.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effects)
            || effects is null
            || !rootResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa)
            || ssa is null)
        {
            return null;
        }

        var entryFunctions = CollectRootHotPathEntryFunctions(syntaxModel, loadedModules, effects);
        var reachableFunctions = new HashSet<string>(
            CollectHotPathReachableFunctions(entryFunctions, ssa, effects),
            StringComparer.Ordinal);
        AddImportedTemplateBoundCallReachability(rootResult, loadedModules, reachableFunctions);
        var seedsByModule = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var module in loadedModules.ImportedModules.Where(static module => !module.Reference.IsExternal))
        {
            var seeds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var qualifiedName in EnumerateImportedFunctionModelNames(module, effects))
            {
                if (reachableFunctions.Contains(qualifiedName)
                    && TryGetImportedModuleLocalFunctionName(module.SyntaxModel.ModuleName, qualifiedName, out var localName))
                {
                    seeds.Add(localName);
                }
            }

            if (seeds.Count != 0)
            {
                seedsByModule[module.SyntaxModel.ModuleName] = seeds;
            }
        }

        return seedsByModule;
    }

    private static IEnumerable<string> EnumerateImportedFunctionModelNames(
        LoadedModuleDocument module,
        FunctionEffectModel effects)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var modulePrefix = $"{module.SyntaxModel.ModuleName}.";

        foreach (var qualifiedName in effects.Functions.Keys)
        {
            if (qualifiedName.StartsWith(modulePrefix, StringComparison.Ordinal)
                && seen.Add(qualifiedName))
            {
                yield return qualifiedName;
            }
        }

        if (module.PackageImageFacts is not { } packageFacts)
        {
            yield break;
        }

        foreach (var qualifiedName in packageFacts.FunctionEffects.Keys)
        {
            if (qualifiedName.StartsWith(modulePrefix, StringComparison.Ordinal)
                && seen.Add(qualifiedName))
            {
                yield return qualifiedName;
            }
        }

        foreach (var qualifiedName in packageFacts.FunctionSignatures.Keys)
        {
            if (qualifiedName.StartsWith(modulePrefix, StringComparison.Ordinal)
                && seen.Add(qualifiedName))
            {
                yield return qualifiedName;
            }
        }

        foreach (var qualifiedName in packageFacts.FunctionTemplates.Keys)
        {
            if (qualifiedName.StartsWith(modulePrefix, StringComparison.Ordinal)
                && seen.Add(qualifiedName))
            {
                yield return qualifiedName;
            }
        }
    }

    private static bool TryGetImportedModuleLocalFunctionName(
        string moduleName,
        string qualifiedName,
        out string localName)
    {
        var modulePrefix = $"{moduleName}.";
        if (!qualifiedName.StartsWith(modulePrefix, StringComparison.Ordinal)
            || qualifiedName.Length == modulePrefix.Length)
        {
            localName = string.Empty;
            return false;
        }

        localName = qualifiedName[modulePrefix.Length..];
        return true;
    }

    private static void AddImportedTemplateBoundCallReachability(
        CompilationResult rootResult,
        LoadedModuleSet loadedModules,
        HashSet<string> reachableFunctions)
    {
        var importedTemplates = new Dictionary<string, ImportedFunctionTemplateSummary>(StringComparer.Ordinal);
        foreach (var module in loadedModules.ImportedModules)
        {
            if (module.PackageImageFacts is not { } packageFacts)
            {
                continue;
            }

            foreach (var (qualifiedName, template) in packageFacts.FunctionTemplates)
            {
                if (template.BoundOperations.Count != 0)
                {
                    importedTemplates[qualifiedName] = template;
                }
            }
        }

        if (importedTemplates.Count == 0)
        {
            return;
        }

        var specializationTemplateNames = rootResult.Artifacts.TryGet(
            CompilerArtifactKeys.SpecializationCodegenStrategy,
            out SpecializationCodegenStrategyModel? specializationStrategy)
            && specializationStrategy is not null
                ? specializationStrategy.Functions.ToDictionary(
                    static function => function.SymbolName,
                    static function => function.TemplateName,
                    StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

        var pending = new Queue<string>();
        foreach (var functionName in reachableFunctions.ToArray())
        {
            pending.Enqueue(functionName);
            if (specializationTemplateNames.TryGetValue(functionName, out var templateName))
            {
                Enqueue(templateName);
            }
        }

        while (pending.Count != 0)
        {
            var functionName = pending.Dequeue();
            if (!importedTemplates.TryGetValue(functionName, out var template))
            {
                continue;
            }

            foreach (var summary in template.BoundOperations)
            {
                switch (summary.Operation)
                {
                    case BoundDirectCallOperation directCall:
                        Enqueue(directCall.Signature.Name);
                        if (directCall.Signature.TemplateName is { } directCallTemplateName)
                        {
                            Enqueue(directCallTemplateName);
                        }

                        break;

                    case BoundMemberCallOperation memberCall:
                        Enqueue(memberCall.Signature.Name);
                        if (memberCall.Signature.TemplateName is { } memberCallTemplateName)
                        {
                            Enqueue(memberCallTemplateName);
                        }

                        break;
                }
            }
        }

        void Enqueue(string functionName)
        {
            if (reachableFunctions.Add(functionName))
            {
                pending.Enqueue(functionName);
            }
        }
    }

    private static IReadOnlySet<string>? ResolveImportedInlineCloneSeedFunctions(
        LoadedModuleDocument module,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? importedInlineCloneSeedsByModule)
    {
        if (importedInlineCloneSeedsByModule is null)
        {
            return null;
        }

        return importedInlineCloneSeedsByModule.TryGetValue(module.SyntaxModel.ModuleName, out var seeds)
            ? seeds
            : EmptyImportedInlineCloneSeedFunctions;
    }

    private static IReadOnlySet<string> CollectRootHotPathEntryFunctions(
        SyntaxModel syntaxModel,
        LoadedModuleSet loadedModules,
        FunctionEffectModel effects)
    {
        if (!loadedModules.TryGet(loadedModules.RootModuleName, out var rootModule) || rootModule is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var rootFunctions = rootModule.SyntaxModel.Declarations
            .Where(static declaration => declaration.Function is not null)
            .Select(declaration => new
            {
                Name = FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration),
                Declaration = declaration
            })
            .ToArray();

        var hotFunctions = rootFunctions
            .Where(function =>
                effects.Functions.TryGetValue(function.Name, out var functionEffects)
                && functionEffects.IsHot
                && !functionEffects.IsCold)
            .Select(static function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (hotFunctions.Count != 0)
        {
            return hotFunctions;
        }

        var exportedFunctions = rootFunctions
            .Where(function =>
                function.Declaration.Visibility == StarkVisibility.Export
                && effects.Functions.TryGetValue(function.Name, out var functionEffects)
                && !functionEffects.IsCold)
            .Select(static function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (exportedFunctions.Count != 0)
        {
            return exportedFunctions;
        }

        return rootFunctions
            .Where(function =>
                !effects.Functions.TryGetValue(function.Name, out var functionEffects)
                || !functionEffects.IsCold)
            .Select(static function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> CollectHotPathReachableFunctions(
        IReadOnlySet<string> entryFunctions,
        SsaIrModule ssa,
        FunctionEffectModel effects)
    {
        var callsByFunction = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var function in ssa.Functions)
        {
            var callees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in function.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is SsaValueInstruction { Value: SsaCallRValue call })
                    {
                        callees.Add(call.FunctionName);
                    }
                    else if (instruction is SsaCallInstruction statementCall)
                    {
                        callees.Add(statementCall.FunctionName);
                    }
                }
            }

            callsByFunction[function.Name] = callees;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(entryFunctions);
        while (pending.Count != 0)
        {
            var functionName = pending.Dequeue();
            if (effects.Functions.TryGetValue(functionName, out var functionEffects)
                && functionEffects.IsCold)
            {
                continue;
            }

            if (!reachable.Add(functionName)
                || !callsByFunction.TryGetValue(functionName, out var callees))
            {
                continue;
            }

            foreach (var callee in callees)
            {
                pending.Enqueue(callee);
            }
        }

        return reachable;
    }

    private static DependencyLlvmCompileResult CompileDependencyLlvm(
        LoadedModuleDocument module,
        CompilerOptions rootOptions,
        IReadOnlySet<string>? importedInlineCloneSeedFunctions)
    {
        if (rootOptions.ModuleResolver is not IModuleSourceResolver sourceResolver
            || !sourceResolver.TryLoadModuleSource(module.Reference, out var sourceText, out var sourceFilePath))
        {
            if (module.Reference.FilePath is null || !File.Exists(module.Reference.FilePath))
            {
                return new DependencyLlvmCompileResult(false, module, null, EmptyLlvmSymbolSummary(), [], [], UsesFilteredOwnedFunctionEmission: importedInlineCloneSeedFunctions is not null, RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false, RequiresNtDllLibrary: false);
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
                QualifyModuleSymbols = true,
                ImportedInlineCloneSeedFunctions = importedInlineCloneSeedFunctions,
                PruneUnusedLoweredFunctions = true
            });

        if (!dependencyResult.Succeeded)
        {
            return new DependencyLlvmCompileResult(false, module, null, EmptyLlvmSymbolSummary(), dependencyResult.Diagnostics, dependencyResult.Logs, UsesFilteredOwnedFunctionEmission: importedInlineCloneSeedFunctions is not null, RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false, RequiresNtDllLibrary: false);
        }

        if (!dependencyResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule) || llvmModule is null)
        {
            return new DependencyLlvmCompileResult(false, module, null, EmptyLlvmSymbolSummary(), [], dependencyResult.Logs, UsesFilteredOwnedFunctionEmission: importedInlineCloneSeedFunctions is not null, RequiresMathLibrary: false, RequiresWinsockLibrary: false, RequiresWindowsSynchronizationLibrary: false, RequiresNtDllLibrary: false);
        }

        var requiresMathLibrary = TargetRequiresExplicitMathLibrary(rootOptions.TargetInfo)
            && LlvmTextRequiresMathLibrary(llvmModule.Text);
        var requiresWinsockLibrary = TargetRequiresWinsockLibrary(rootOptions.TargetInfo)
            && LlvmTextRequiresWinsockLibrary(llvmModule.Text);
        var requiresWindowsSynchronizationLibrary = TargetRequiresWindowsSynchronizationLibrary(rootOptions.TargetInfo)
            && LlvmTextRequiresWindowsSynchronizationLibrary(llvmModule.Text);
        var requiresNtDllLibrary = TargetRequiresNtDllLibrary(rootOptions.TargetInfo)
            && LlvmTextRequiresNtDllLibrary(llvmModule.Text);
        return new DependencyLlvmCompileResult(
            true,
            module,
            llvmModule.Text,
            SummarizeLlvmSymbols(llvmModule.Text),
            [],
            dependencyResult.Logs,
            importedInlineCloneSeedFunctions is not null,
            requiresMathLibrary,
            requiresWinsockLibrary,
            requiresWindowsSynchronizationLibrary,
            requiresNtDllLibrary);
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
            enableLto: enableLto);
    }

    private static bool ShouldEnableExecutableLto(string? linkerTool)
    {
        if (!string.IsNullOrWhiteSpace(linkerTool)
            && !Path.GetFileName(linkerTool).Contains("clang", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NativeToolchain.SupportsExecutableThinLto();
    }

    private static bool ShouldEnableLibraryLto()
    {
        return NativeToolchain.SupportsExecutableThinLto();
    }

    internal static bool ShouldEnableDependencyLto(LoadedModuleDocument module)
    {
        return AnalyzeModuleOptimizationSafety(module, toolchainCanUseThinLto: true).CanEmitThinLtoBitcode;
    }

    internal static ModuleOptimizationSafetyFacts AnalyzeModuleOptimizationSafety(
        LoadedModuleDocument module,
        bool toolchainCanUseThinLto)
    {
        var isBackendOpaque = IsBackendOpaque(module);
        var containsStoredBorrow = ModuleContainsStoreBorrowSyntax(module);
        var exposesHotInlineCandidates = ModuleExposesHotInlineCandidates(module);
        var canEmitThinLtoBitcode = toolchainCanUseThinLto && !isBackendOpaque && !containsStoredBorrow;
        var reason = canEmitThinLtoBitcode
            ? exposesHotInlineCandidates
                ? "thinlto-enabled-hot-inline-candidates"
                : "thinlto-enabled"
            : !toolchainCanUseThinLto
                ? "thinlto-unavailable"
                : isBackendOpaque
                    ? "backend-opaque"
                    : "stored-borrow-aliasing";

        return new ModuleOptimizationSafetyFacts(
            module.SyntaxModel.ModuleName,
            CanEmitThinLtoBitcode: canEmitThinLtoBitcode,
            CanRunNormalLlvmPasses: canEmitThinLtoBitcode,
            ContainsKnownFragileConstructs: isBackendOpaque || containsStoredBorrow,
            ExposesHotInlineCandidates: exposesHotInlineCandidates,
            reason);
    }

    private static ModuleOptimizationSafetyFacts AddOptimizationDecision(
        ToolchainMetrics? toolchainMetrics,
        string scope,
        ModuleOptimizationSafetyFacts facts)
    {
        toolchainMetrics?.AddOptimizationDecision(scope, facts);
        return facts;
    }

    internal static bool ShouldEnableRootModuleLto(CompilationResult result)
    {
        if (result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
            && syntaxModel?.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque)
        {
            return false;
        }

        return !result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel)
            || typeModel is null
            || syntaxModel is null
            || !RootTypeCheckModelContainsStoredBorrow(syntaxModel, typeModel);
    }

    private static bool ModuleContainsStoreBorrowSyntax(LoadedModuleDocument module)
    {
        return module.ParseResult.SourceText.Contains("storeborrow", StringComparison.Ordinal);
    }

    private static bool TypeCheckModelContainsStoredBorrow(TypeCheckModel typeModel)
    {
        foreach (var namedType in typeModel.NamedTypes.Values)
        {
            foreach (var field in namedType.OrderedFields)
            {
                if (TypeCanReachStoredBorrow(field.Type, typeModel.NamedTypes, new HashSet<string>(StringComparer.Ordinal)))
                {
                    return true;
                }
            }
        }

        foreach (var function in typeModel.Functions.Values)
        {
            if (TypeCanReachStoredBorrow(function.ReturnType, typeModel.NamedTypes, new HashSet<string>(StringComparer.Ordinal)))
            {
                return true;
            }

            foreach (var parameter in function.Parameters)
            {
                if (TypeCanReachStoredBorrow(parameter.Type, typeModel.NamedTypes, new HashSet<string>(StringComparer.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool RootTypeCheckModelContainsStoredBorrow(
        SyntaxModel syntaxModel,
        TypeCheckModel typeModel)
    {
        foreach (var declaration in syntaxModel.Declarations)
        {
            if (declaration.Function is not null)
            {
                var functionName = FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, declaration);
                if (typeModel.Functions.TryGetValue(functionName, out var function)
                    || typeModel.Functions.TryGetValue($"{syntaxModel.ModuleName}.{functionName}", out function))
                {
                    if (TypeCanReachStoredBorrow(function.ReturnType, typeModel.NamedTypes, new HashSet<string>(StringComparer.Ordinal)))
                    {
                        return true;
                    }

                    foreach (var parameter in function.Parameters)
                    {
                        if (TypeCanReachStoredBorrow(parameter.Type, typeModel.NamedTypes, new HashSet<string>(StringComparer.Ordinal)))
                        {
                            return true;
                        }
                    }
                }
            }

            if (declaration.Kind is DeclarationKind.Struct or DeclarationKind.Enum
                && (typeModel.NamedTypes.TryGetValue(declaration.Name, out var namedType)
                    || typeModel.NamedTypes.TryGetValue($"{syntaxModel.ModuleName}.{declaration.Name}", out namedType)))
            {
                foreach (var field in namedType.OrderedFields)
                {
                    if (TypeCanReachStoredBorrow(field.Type, typeModel.NamedTypes, new HashSet<string>(StringComparer.Ordinal)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TypeCanReachStoredBorrow(
        StarkTypeSymbol type,
        IReadOnlyDictionary<string, NamedTypeSymbol> namedTypes,
        HashSet<string> visitedNamedTypes)
    {
        if (type.BorrowKind == StarkBorrowKind.StoreBorrow)
        {
            return true;
        }

        var valueType = StarkTypeSymbols.BorrowReturnValueType(type);
        if (valueType.Kind == StarkTypeKind.FixedArray && valueType.ElementType is not null)
        {
            return TypeCanReachStoredBorrow(valueType.ElementType, namedTypes, visitedNamedTypes);
        }

        if (valueType.Kind != StarkTypeKind.Named
            || valueType.NamedType is not { } namedTypeName
            || !visitedNamedTypes.Add(namedTypeName)
            || !namedTypes.TryGetValue(namedTypeName, out var namedType))
        {
            return false;
        }

        foreach (var field in namedType.OrderedFields)
        {
            if (TypeCanReachStoredBorrow(field.Type, namedTypes, visitedNamedTypes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBackendOpaque(LoadedModuleDocument module)
    {
        return module.SyntaxModel.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
            || module.PackageImageFacts?.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque;
    }

    private static bool ModuleExposesHotInlineCandidates(LoadedModuleDocument module)
    {
        if (module.PackageImageFacts is not null)
        {
            return module.PackageImageFacts.FunctionEffects.Values.Any(static function =>
                function.BackendOptimizationMode != ModuleBackendOptimizationMode.Opaque
                && (function.IsHot || function.InlinePreference == InlinePreference.Inline));
        }

        return module.SyntaxModel.Declarations.Any(static declaration =>
            declaration.Function is { HasBody: true } function
            && function.BackendOptimizationMode != ModuleBackendOptimizationMode.Opaque
            && (function.Modifiers.IsHot
                || function.Modifiers.InlinePreference == InlinePreference.Inline
                || FunctionKindFacts.IsLaw(function.Kind)));
    }

    private static LlvmSymbolSummary SummarizeLlvmSymbols(string llvmText)
    {
        var allDefinedSymbols = new HashSet<string>(StringComparer.Ordinal);
        var linkerVisibleDefinedSymbols = new HashSet<string>(StringComparer.Ordinal);
        var referencedSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in llvmText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (IsPureLlvmDeclarationLine(rawLine))
            {
                continue;
            }

            if (TryReadDefinedLlvmSymbol(rawLine, out var definedSymbol, out var isLinkerVisible))
            {
                allDefinedSymbols.Add(definedSymbol);
                if (isLinkerVisible)
                {
                    linkerVisibleDefinedSymbols.Add(definedSymbol);
                }
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

        referencedSymbols.ExceptWith(allDefinedSymbols);
        return new LlvmSymbolSummary(linkerVisibleDefinedSymbols, referencedSymbols);
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

    private static bool TryReadDefinedLlvmSymbol(string line, out string symbol, out bool isLinkerVisible)
    {
        symbol = string.Empty;
        isLinkerVisible = false;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("define ", StringComparison.Ordinal))
        {
            var atIndex = trimmed.IndexOf('@');
            if (atIndex < 0 || !TryReadLlvmSymbolAt(trimmed, atIndex, out symbol, out _))
            {
                return false;
            }

            isLinkerVisible = IsLinkerVisibleFunctionDefinition(trimmed[..atIndex]);
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
        if (initializer.StartsWith("external", StringComparison.Ordinal)
            || initializer.StartsWith("extern_weak", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadLlvmSymbolAt(trimmed, 0, out symbol, out _))
        {
            return false;
        }

        isLinkerVisible = IsLinkerVisibleGlobalInitializer(initializer);
        return true;
    }

    private static bool IsLinkerVisibleFunctionDefinition(string definitionPrefix)
    {
        foreach (var token in definitionPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "internal" or "private" or "available_externally")
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLinkerVisibleGlobalInitializer(string initializer)
    {
        var firstToken = initializer.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstToken is not ("internal" or "private" or "available_externally");
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
        await stdout.WriteLineAsync("  (default)      Build an executable when the root source exports main; otherwise build a library");
        await stdout.WriteLineAsync("  --check       Validate through ownership/lifetime analysis");
        await stdout.WriteLineAsync("  --emit-mir    Print lowered MIR");
        await stdout.WriteLineAsync("  --emit-ssa    Print lowered SSA");
        await stdout.WriteLineAsync("  --emit-llvm   Print emitted LLVM IR");
        await stdout.WriteLineAsync("  --emit-obj    Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --compile-only Compile LLVM IR to an object file");
        await stdout.WriteLineAsync("  --emit-lib    Build a static library and Stark package image");
        await stdout.WriteLineAsync("  --emit-pkg    Emit a Stark package image without linker/archive steps\n  --package-image-json    Also write the indented JSON inspection sidecar next to the binary package image");
        await stdout.WriteLineAsync("  --emit-package Emit a Stark package image without linker/archive steps");
        await stdout.WriteLineAsync("  --inspect-pkg Inspect and validate a Stark package image");
        await stdout.WriteLineAsync("  --inspect-package Inspect and validate a Stark package image");
        await stdout.WriteLineAsync("  --host-test-inspect [path] Run structured host-compiler test inspection JSON");
        await stdout.WriteLineAsync("  --host-test-server Run persistent newline-delimited host-test inspection");
        await stdout.WriteLineAsync("  --emit-exe    Build a native executable");
        await stdout.WriteLineAsync("  --link-only   Build a native executable from the current compilation output");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Inputs and Outputs:");
        await stdout.WriteLineAsync("  [path]                 Read a Stark source file instead of stdin");
        await stdout.WriteLineAsync("  -o <path>              Write the selected output artifact to <path>");
        await stdout.WriteLineAsync("  --package-library-file <name>  Library file name stored in emitted package images");
        await stdout.WriteLineAsync("  -I, --search-dir <dir> Add a Stark module/package search directory");
        await stdout.WriteLineAsync("  --no-stark-path       Ignore STARK_PATH module/package search entries");
        await stdout.WriteLineAsync("  -L, --library-dir <dir> Add a native library search directory for linking");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Targeting and Native Toolchain:");
        await stdout.WriteLineAsync("  --target <triple>              Override the LLVM target triple");
        await stdout.WriteLineAsync("  --target-data-layout <layout>  Override the LLVM target data layout");
        await stdout.WriteLineAsync("  --target-cpu <cpu>             Forward an explicit target CPU to native codegen (for example: znver4)");
        await stdout.WriteLineAsync("  --target-feature <feature>     Forward a target feature string; repeatable (for example: +sse4.1)");
        await stdout.WriteLineAsync("  --relocation-model <default|static|pic|pie>  Choose the native relocation/PIC model");
        await stdout.WriteLineAsync("  --code-model <tiny|small|kernel|medium|large>  Forward an explicit LLVM code model");
        await stdout.WriteLineAsync("  --strict-integer-ranges        Keep strict integer range storage checks enabled (default)");
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
        await stdout.WriteLineAsync("  --package-image-output <path>  Write the --emit-lib package image to a specific path");
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
        await stdout.WriteLineAsync("  --emit-lib and --emit-exe force the archive/link workflow.");
        await stdout.WriteLineAsync("  --emit-pkg validates and emits package image JSON only.");
        await stdout.WriteLineAsync("  --inspect-pkg accepts a binary .starkpkg or legacy .starkpkg.json file path, or JSON from stdin.");
        await stdout.WriteLineAsync("  --host-test-inspect and --host-test-server are for Stark-native tests targeting the current host compiler.");
        await stdout.WriteLineAsync("  With no workflow flag, the compiler infers executable vs library from the root source.");
        await stdout.WriteLineAsync("  --diagnostic-format json suppresses the text compiler log stream so stderr stays machine-readable.");
        await stdout.WriteLineAsync("  Library/package search uses -I/--search-dir and the STARK_PATH environment variable unless --no-stark-path is passed.");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Examples:");
        await stdout.WriteLineAsync("  compiler app.stark");
        await stdout.WriteLineAsync("  compiler app.stark --check");
        await stdout.WriteLineAsync("  compiler app.stark --emit-llvm -o app.ll");
        await stdout.WriteLineAsync("  compiler libFacade.stark --emit-lib -o libFacade.a");
        await stdout.WriteLineAsync("  compiler app.stark --emit-pkg -o app.starkpkg");
        await stdout.WriteLineAsync("  compiler libFacade.starkpkg --inspect-pkg");
        await stdout.WriteLineAsync("  compiler app.stark --diagnostic-format json");
        await stdout.WriteLineAsync("  compiler --host-test-inspect request.json");
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
        // An explicit --target steers target-dependent decisions (asm variant selection,
        // package facts) in every mode; only host-toolchain detection stays reserved for
        // the modes that need native output.
        var explicitTargetInfo = CreateTargetInfo(targetTriple, targetDataLayout, targetCpu, targetFeatures, relocationModel, codeModel);
        if (explicitTargetInfo is not null)
        {
            return explicitTargetInfo;
        }

        if (!requiresTargetInfo)
        {
            return null;
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

    private static CliMode InferDefaultBuildMode(string source, LlvmTargetInfo? targetInfo)
    {
        var parseResult = StarkSyntax.ParseCompilationUnit(source);
        if (!parseResult.Succeeded)
        {
            return CliMode.EmitExecutable;
        }

        var syntaxModel = SyntaxModelFactory.CreateWithDiagnostics(parseResult, targetInfo).Model;
        var hasHostedEntrypoint = DeclaredFunctionSyntaxCollector
            .Collect(parseResult, syntaxModel)
            .Any(static function =>
                function.ContainingTypeName is null
                && function.Visibility == StarkVisibility.Export
                && function.HasBody
                && string.Equals(function.SourceName, "main", StringComparison.Ordinal));

        return hasHostedEntrypoint ? CliMode.EmitExecutable : CliMode.EmitLibrary;
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

    private static string DeriveExecutableOutputPath(
        string? inputPath,
        CompilationResult result,
        LlvmTargetInfo? targetInfo)
    {
        var moduleName = result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
                         && syntaxModel is not null
            ? syntaxModel.ModuleName
            : null;

        return DeriveExecutableOutputPath(inputPath, moduleName, targetInfo);
    }

    internal static string DeriveExecutableOutputPath(
        string? inputPath,
        string? moduleName,
        LlvmTargetInfo? targetInfo)
    {
        string outputPath;
        if (inputPath is not null)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var directory = Path.GetDirectoryName(fullInputPath) ?? Environment.CurrentDirectory;
            outputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullInputPath));
            return AddWindowsExecutableExtensionIfNeeded(outputPath, targetInfo);
        }

        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            outputPath = Path.GetFullPath(moduleName);
            return AddWindowsExecutableExtensionIfNeeded(outputPath, targetInfo);
        }

        outputPath = IsWindowsTarget(targetInfo) ? "a" : "a.out";
        return AddWindowsExecutableExtensionIfNeeded(Path.GetFullPath(outputPath), targetInfo);
    }

    private static string AddWindowsExecutableExtensionIfNeeded(string outputPath, LlvmTargetInfo? targetInfo)
    {
        return IsWindowsTarget(targetInfo)
               && !outputPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? $"{outputPath}.exe"
            : outputPath;
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

    private static string DeriveLibraryManifestPath(string libraryOutputPath, string? inputPath, CompilationResult result)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(libraryOutputPath)) ?? Environment.CurrentDirectory;
        var emittedLibraryFileName = Path.GetFileName(libraryOutputPath);
        var canonicalLibraryFileName = ResolvePackageLibraryFileName(null, inputPath, result);
        var baseName = GetLibraryPackageImageBaseName(emittedLibraryFileName, canonicalLibraryFileName);
        return Path.Combine(directory, $"{baseName}{PackageImageBinaryFormat.FileExtension}");
    }

    private static string GetLibraryPackageImageBaseName(string emittedLibraryFileName, string canonicalLibraryFileName)
    {
        var emittedBaseName = Path.GetFileNameWithoutExtension(emittedLibraryFileName);
        var canonicalBaseName = Path.GetFileNameWithoutExtension(canonicalLibraryFileName);

        if (OperatingSystem.IsWindows()
            && canonicalBaseName.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
            && emittedLibraryFileName.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
            && string.Equals(emittedBaseName, canonicalBaseName["lib".Length..], StringComparison.OrdinalIgnoreCase))
        {
            return canonicalBaseName;
        }

        return emittedBaseName;
    }

    private static string BuildPackageLibraryReference(string libraryOutputPath, string manifestPath)
    {
        var libraryFullPath = Path.GetFullPath(libraryOutputPath);
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Environment.CurrentDirectory;
        var relativePath = Path.GetRelativePath(manifestDirectory, libraryFullPath);
        return string.IsNullOrWhiteSpace(relativePath)
            ? Path.GetFileName(libraryFullPath)
            : relativePath;
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
        var packageImageFileName = $"{Path.GetFileNameWithoutExtension(packageLibraryFileName)}{PackageImageBinaryFormat.FileExtension}";

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
        bool RequiresWindowsSynchronizationLibrary,
        bool RequiresNtDllLibrary);

    private sealed record DependencyLlvmCompileResult(
        bool Success,
        LoadedModuleDocument Module,
        string? LlvmText,
        LlvmSymbolSummary Symbols,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        IReadOnlyList<CompilerLogEntry> Logs,
        bool UsesFilteredOwnedFunctionEmission,
        bool RequiresMathLibrary,
        bool RequiresWinsockLibrary,
        bool RequiresWindowsSynchronizationLibrary,
        bool RequiresNtDllLibrary);

    private sealed record SourceDependencyLinkResult(
        bool Success,
        IReadOnlyList<string> ObjectPaths,
        IReadOnlyList<CompilerDiagnostic> Diagnostics,
        IReadOnlyList<CompilerLogEntry> Logs,
        NativeToolchainResult? ToolchainResult,
        bool RequiresMathLibrary,
        bool RequiresWinsockLibrary,
        bool RequiresWindowsSynchronizationLibrary,
        bool RequiresNtDllLibrary);

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
        private readonly List<string> optimizationDecisions = [];

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

        public void AddOptimizationDecision(string scope, ModuleOptimizationSafetyFacts facts)
        {
            optimizationDecisions.Add(
                string.Join(
                    ',',
                    [
                        $"scope={scope}",
                        $"module={facts.ModuleName}",
                        $"thinlto={FormatBool(facts.CanEmitThinLtoBitcode)}",
                        $"llvm_passes={FormatBool(facts.CanRunNormalLlvmPasses)}",
                        $"fragile={FormatBool(facts.ContainsKnownFragileConstructs)}",
                        $"hot_inline={FormatBool(facts.ExposesHotInlineCandidates)}",
                        $"reason={facts.DecisionReason}"
                    ]));
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
                        $"optimization_decision_count={optimizationDecisions.Count}",
                        .. optimizationDecisions.Select((decision, index) => $"optimization_decision_{index}={decision}"),
                        $"toolchain_us={LlvmObjectMicroseconds + NativeObjectMicroseconds + LinkMicroseconds + ArchiveMicroseconds}"
                    ])
                + Environment.NewLine);
        }

        private static string FormatBool(bool value) => value ? "true" : "false";

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
