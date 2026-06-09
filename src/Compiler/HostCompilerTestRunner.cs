using System.Diagnostics;
using System.Text.Json;

namespace Stark.Compiler;

internal static class HostCompilerTestRunner
{
    private const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    private static readonly JsonSerializerOptions CompactWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions IndentedWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static bool IsCommand(string argument)
    {
        return argument is "--host-test-inspect" or "--host-test-server";
    }

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || !IsCommand(args[0]))
        {
            await stderr.WriteLineAsync("Expected --host-test-inspect or --host-test-server.");
            return 1;
        }

        return args[0] switch
        {
            "--host-test-inspect" => await RunInspectAsync(args, stdin, stdout, stderr),
            "--host-test-server" => await RunServerAsync(args, stdin, stdout, stderr),
            _ => 1
        };
    }

    private static async Task<int> RunInspectAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length > 2)
        {
            await stderr.WriteLineAsync("Usage: compiler --host-test-inspect [request-json-path]");
            return 1;
        }

        string json;
        if (args.Length == 2)
        {
            try
            {
                json = await File.ReadAllTextAsync(Path.GetFullPath(args[1]));
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"Could not read host test request '{args[1]}': {ex.Message}");
                return 1;
            }
        }
        else
        {
            json = await stdin.ReadToEndAsync();
        }

        if (!TryParseDocument(json, out var document, out var error))
        {
            await stderr.WriteLineAsync(error);
            return 1;
        }

        var response = new HostCompilerTestSession().Execute(document!);
        await stdout.WriteLineAsync(Serialize(response, indented: true));
        return 0;
    }

    private static async Task<int> RunServerAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 1)
        {
            await stderr.WriteLineAsync("Usage: compiler --host-test-server");
            return 1;
        }

        var session = new HostCompilerTestSession();
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseDocument(line, out var document, out var error))
            {
                await stdout.WriteLineAsync(Serialize(
                    HostCompilerTestDocumentResponse.FromProtocolError(error),
                    indented: false));
                await stdout.FlushAsync();
                continue;
            }

            if (document!.Shutdown)
            {
                await stdout.WriteLineAsync(Serialize(HostCompilerTestDocumentResponse.FromShutdown(), indented: false));
                await stdout.FlushAsync();
                return 0;
            }

            await stdout.WriteLineAsync(Serialize(session.Execute(document), indented: false));
            await stdout.FlushAsync();
        }

        return 0;
    }

    private static bool TryParseDocument(
        string json,
        out HostCompilerTestDocument? document,
        out string error)
    {
        document = null;
        error = string.Empty;

        try
        {
            document = JsonSerializer.Deserialize<HostCompilerTestDocument>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid host test request JSON: {ex.Message}";
            return false;
        }

        if (document is null)
        {
            error = "Invalid host test request JSON: empty document.";
            return false;
        }

        return true;
    }

    private static string Serialize(HostCompilerTestDocumentResponse response, bool indented)
    {
        return JsonSerializer.Serialize(response, indented ? IndentedWriteOptions : CompactWriteOptions);
    }

    private sealed class HostCompilerTestSession
    {
        private readonly CompilerPipeline _pipeline = DefaultCompilerPipeline.Create();

        public HostCompilerTestDocumentResponse Execute(HostCompilerTestDocument document)
        {
            if (document.ProtocolVersion != ProtocolVersion)
            {
                return HostCompilerTestDocumentResponse.FromProtocolError(
                    $"Unsupported host test protocol version {document.ProtocolVersion}; expected {ProtocolVersion}.");
            }

            var requests = CollectRequests(document);
            if (requests.Count == 0)
            {
                return HostCompilerTestDocumentResponse.FromProtocolError(
                    "Host test request document must contain 'request' or a non-empty 'requests' array.");
            }

            var responses = new List<HostCompilerTestCompileResponse>(requests.Count);
            foreach (var request in requests)
            {
                responses.Add(ExecuteCompile(request));
            }

            return new HostCompilerTestDocumentResponse(
                ProtocolVersion,
                Shutdown: false,
                Responses: responses,
                ProtocolError: null);
        }

        private HostCompilerTestCompileResponse ExecuteCompile(HostCompilerTestCompileRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!TryLoadSource(request, out var sourceText, out var filePath, out var sourceError))
                {
                    stopwatch.Stop();
                    return HostCompilerTestCompileResponse.FromProtocolError(
                        request.Id,
                        sourceError,
                        ToMicroseconds(stopwatch.Elapsed));
                }

                var artifactRequests = NormalizeArtifactRequests(request.Artifacts);
                var options = BuildOptions(request, filePath, artifactRequests);
                CompilationResult result;

                using (CompilerLogOutput.Push(TextWriter.Null, DiagnosticSeverity.Info, CompilerLogVerbosity.Verbose))
                {
                    result = _pipeline.Run(new CompilationInput(sourceText, filePath), options);
                }

                stopwatch.Stop();
                return BuildResponse(request, result, artifactRequests, ToMicroseconds(stopwatch.Elapsed));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return HostCompilerTestCompileResponse.FromProtocolError(
                    request.Id,
                    $"{ex.GetType().Name}: {ex.Message}",
                    ToMicroseconds(stopwatch.Elapsed));
            }
        }

        private static bool TryLoadSource(
            HostCompilerTestCompileRequest request,
            out string sourceText,
            out string? filePath,
            out string error)
        {
            sourceText = string.Empty;
            filePath = string.IsNullOrWhiteSpace(request.FilePath) ? null : Path.GetFullPath(request.FilePath);
            error = string.Empty;

            if (request.SourceText is not null)
            {
                sourceText = request.SourceText;
                return true;
            }

            if (string.IsNullOrWhiteSpace(request.SourcePath))
            {
                error = "Compile request must provide sourceText or sourcePath.";
                return false;
            }

            var sourcePath = Path.GetFullPath(request.SourcePath);
            try
            {
                sourceText = File.ReadAllText(sourcePath);
                filePath ??= sourcePath;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not read sourcePath '{sourcePath}': {ex.Message}";
                return false;
            }
        }

        private static CompilerOptions BuildOptions(
            HostCompilerTestCompileRequest request,
            string? filePath,
            IReadOnlyList<HostCompilerArtifactRequest> artifactRequests)
        {
            return new CompilerOptions(
                EmitLlvmIr: request.EmitLlvmIr
                    ?? artifactRequests.Any(static artifact =>
                        string.Equals(artifact.ArtifactName, CompilerArtifactKeys.LlvmIrModule.Name, StringComparison.Ordinal)),
                ContinueAfterErrors: request.ContinueAfterErrors ?? false,
                ModuleResolver: BuildModuleResolver(request, filePath),
                StopAfterPassId: string.IsNullOrWhiteSpace(request.StopAfterPassId) ? null : request.StopAfterPassId,
                TargetInfo: BuildTargetInfo(request),
                QualifyModuleSymbols: request.QualifyModuleSymbols ?? false,
                OptimizationLevel: ParseOptimizationLevel(request.OptimizationLevel),
                InternalizeModulePrivate: request.InternalizeModulePrivate ?? false,
                EnforceIntegerRangeStorageRules: request.StrictIntegerRanges ?? true,
                ImportedInlineCloneSeedFunctions: request.ImportedInlineCloneSeedFunctions is { Count: > 0 } seeds
                    ? new HashSet<string>(seeds, StringComparer.Ordinal)
                    : null,
                MaximumCompileTimeLoopIterations: request.MaximumCompileTimeLoopIterations is > 0
                    ? request.MaximumCompileTimeLoopIterations.Value
                    : CompileTimeFunctionEvaluator.DefaultMaximumCompileTimeLoopIterations);
        }

        private static IModuleResolver? BuildModuleResolver(HostCompilerTestCompileRequest request, string? filePath)
        {
            var directories = new List<string>();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    directories.Add(directory);
                }
            }

            if (request.SearchDirectories is { Count: > 0 })
            {
                directories.AddRange(request.SearchDirectories.Where(static path => !string.IsNullOrWhiteSpace(path)));
            }

            if (request.UseStarkPath == true)
            {
                var environmentSearchPath = Environment.GetEnvironmentVariable("STARK_PATH");
                if (!string.IsNullOrWhiteSpace(environmentSearchPath))
                {
                    directories.AddRange(environmentSearchPath.Split(
                        Path.PathSeparator,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }

            var resolved = directories
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return resolved.Length == 0 ? null : new FileSystemModuleResolver(resolved);
        }

        private static LlvmTargetInfo? BuildTargetInfo(HostCompilerTestCompileRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TargetTriple))
            {
                return null;
            }

            return new LlvmTargetInfo(
                request.TargetTriple.Trim(),
                string.IsNullOrWhiteSpace(request.TargetDataLayout) ? null : request.TargetDataLayout,
                string.IsNullOrWhiteSpace(request.TargetCpu) ? null : request.TargetCpu,
                request.TargetFeatures is { Count: > 0 } ? request.TargetFeatures.ToArray() : null,
                ParseRelocationModel(request.RelocationModel),
                ParseCodeModel(request.CodeModel));
        }

        private static CompilerOptimizationLevel ParseOptimizationLevel(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "0" or "o0" => CompilerOptimizationLevel.O0,
                "g" or "og" => CompilerOptimizationLevel.Og,
                "1" or "o1" => CompilerOptimizationLevel.O1,
                "2" or "o2" => CompilerOptimizationLevel.O2,
                "3" or "o3" or null or "" => CompilerOptimizationLevel.O3,
                _ => CompilerOptimizationLevel.O3
            };
        }

        private static LlvmRelocationModel ParseRelocationModel(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "static" => LlvmRelocationModel.Static,
                "pic" => LlvmRelocationModel.Pic,
                "pie" => LlvmRelocationModel.Pie,
                _ => LlvmRelocationModel.Default
            };
        }

        private static LlvmCodeModel? ParseCodeModel(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "tiny" => LlvmCodeModel.Tiny,
                "small" => LlvmCodeModel.Small,
                "kernel" => LlvmCodeModel.Kernel,
                "medium" => LlvmCodeModel.Medium,
                "large" => LlvmCodeModel.Large,
                _ => null
            };
        }

        private static HostCompilerTestCompileResponse BuildResponse(
            HostCompilerTestCompileRequest request,
            CompilationResult result,
            IReadOnlyList<HostCompilerArtifactRequest> artifactRequests,
            long durationMicroseconds)
        {
            var availableArtifacts = result.Artifacts.Names;
            var artifactTexts = new List<HostCompilerTestArtifactText>();
            var artifactFiles = new List<HostCompilerTestArtifactFile>();
            var missingArtifacts = new List<string>();
            var unsupportedArtifacts = new List<string>();
            var outputErrors = new List<string>();
            var includeArtifactTexts = request.IncludeArtifactTexts ?? true;

            foreach (var artifactRequest in artifactRequests)
            {
                switch (TryRenderArtifact(result, artifactRequest.ArtifactName, out var text))
                {
                    case ArtifactRenderStatus.Rendered:
                        if (includeArtifactTexts)
                        {
                            artifactTexts.Add(new HostCompilerTestArtifactText(
                                artifactRequest.ArtifactName,
                                artifactRequest.RequestedName,
                                text));
                        }

                        if (!string.IsNullOrWhiteSpace(request.ArtifactOutputDirectory)
                            && TryWriteArtifactText(
                                request.ArtifactOutputDirectory!,
                                artifactRequest.ArtifactName,
                                text,
                                outputErrors,
                                out var artifactPath))
                        {
                            artifactFiles.Add(new HostCompilerTestArtifactFile(
                                artifactRequest.ArtifactName,
                                artifactRequest.RequestedName,
                                artifactPath));
                        }

                        break;
                    case ArtifactRenderStatus.Missing:
                        missingArtifacts.Add(artifactRequest.RequestedName);
                        break;
                    case ArtifactRenderStatus.Unsupported:
                        unsupportedArtifacts.Add(artifactRequest.RequestedName);
                        break;
                }
            }

            var diagnostics = result.Diagnostics.Select(ToDiagnostic).ToArray();
            var materializeLogs = request.IncludeLogs == true || !string.IsNullOrWhiteSpace(request.LogsOutputPath);
            var logs = materializeLogs
                ? result.Logs.Select(ToLog).ToArray()
                : [];
            var materializeExecutions = request.IncludeExecutions != false || !string.IsNullOrWhiteSpace(request.ExecutionsOutputPath);
            var executions = materializeExecutions
                ? result.Executions.Select(ToExecution).ToArray()
                : [];
            var responseLogs = request.IncludeLogs == true
                ? logs
                : [];
            var responseExecutions = request.IncludeExecutions == false
                ? []
                : executions;
            var diagnosticsOutputPath = TryWriteDiagnostics(
                request.DiagnosticsOutputPath,
                SummarizeDiagnostics(result.Diagnostics),
                diagnostics,
                outputErrors);
            var logsOutputPath = TryWriteJsonFile(request.LogsOutputPath, logs, outputErrors);
            var executionsOutputPath = TryWriteJsonFile(request.ExecutionsOutputPath, executions, outputErrors);

            return new HostCompilerTestCompileResponse(
                request.Id,
                Succeeded: result.Succeeded,
                ProtocolError: null,
                DurationMicroseconds: durationMicroseconds,
                DiagnosticSummary: SummarizeDiagnostics(result.Diagnostics),
                Diagnostics: diagnostics,
                Logs: responseLogs,
                Executions: responseExecutions,
                AvailableArtifacts: availableArtifacts,
                ArtifactTexts: artifactTexts,
                ArtifactFiles: artifactFiles,
                MissingArtifacts: missingArtifacts,
                UnsupportedArtifacts: unsupportedArtifacts,
                DiagnosticsOutputPath: diagnosticsOutputPath,
                LogsOutputPath: logsOutputPath,
                ExecutionsOutputPath: executionsOutputPath,
                OutputErrors: outputErrors,
                RootModuleName: TryGetRootModuleName(result),
                LoadedModules: GetLoadedModuleNames(result));
        }

        private static bool TryWriteArtifactText(
            string outputDirectory,
            string artifactName,
            string text,
            List<string> outputErrors,
            out string artifactPath)
        {
            artifactPath = string.Empty;

            try
            {
                var directory = Path.GetFullPath(outputDirectory);
                Directory.CreateDirectory(directory);
                artifactPath = Path.Combine(directory, GetArtifactFileName(artifactName));
                File.WriteAllText(artifactPath, text);
                return true;
            }
            catch (Exception ex)
            {
                outputErrors.Add($"Could not write artifact '{artifactName}' to '{outputDirectory}': {ex.Message}");
                return false;
            }
        }

        private static string? TryWriteDiagnostics(
            string? outputPath,
            HostCompilerTestDiagnosticSummary summary,
            IReadOnlyList<HostCompilerTestDiagnostic> diagnostics,
            List<string> outputErrors)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return null;
            }

            return TryWriteJsonFile(
                outputPath,
                new HostCompilerTestDiagnosticOutput(summary, diagnostics),
                outputErrors);
        }

        private static string? TryWriteJsonFile<T>(string? outputPath, T value, List<string> outputErrors)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(outputPath);
            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, JsonSerializer.Serialize(value, IndentedWriteOptions));
                return fullPath;
            }
            catch (Exception ex)
            {
                outputErrors.Add($"Could not write host test output '{fullPath}': {ex.Message}");
                return null;
            }
        }

        private static string GetArtifactFileName(string artifactName)
        {
            if (string.Equals(artifactName, CompilerArtifactKeys.LlvmIrModule.Name, StringComparison.Ordinal))
            {
                return "llvm-ir-module.ll";
            }

            if (string.Equals(artifactName, CompilerArtifactKeys.MidLevelIr.Name, StringComparison.Ordinal))
            {
                return "mid-level-ir.mir";
            }

            if (string.Equals(artifactName, CompilerArtifactKeys.SsaIr.Name, StringComparison.Ordinal))
            {
                return "ssa-ir.ssa";
            }

            if (string.Equals(artifactName, CompilerArtifactKeys.OptimizedSsaIr.Name, StringComparison.Ordinal))
            {
                return "optimized-ssa-ir.ssa";
            }

            return $"{SanitizeFileName(artifactName)}.txt";
        }

        private static string SanitizeFileName(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var ch in value)
            {
                builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'
                    ? ch
                    : '_');
            }

            return builder.Length == 0 ? "artifact" : builder.ToString();
        }

        private static string? TryGetRootModuleName(CompilationResult result)
        {
            return result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel)
                ? syntaxModel?.ModuleName
                : null;
        }

        private static IReadOnlyList<string> GetLoadedModuleNames(CompilationResult result)
        {
            return result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules)
                && loadedModules is not null
                    ? loadedModules.Modules.Keys.Order(StringComparer.Ordinal).ToArray()
                    : [];
        }

        private static HostCompilerTestDiagnosticSummary SummarizeDiagnostics(IReadOnlyList<CompilerDiagnostic> diagnostics)
        {
            return new HostCompilerTestDiagnosticSummary(
                diagnostics.Count,
                diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning),
                diagnostics.Count(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Info));
        }

        private static HostCompilerTestDiagnostic ToDiagnostic(CompilerDiagnostic diagnostic)
        {
            return new HostCompilerTestDiagnostic(
                diagnostic.Code,
                FormatSeverity(diagnostic.Severity),
                diagnostic.Message,
                diagnostic.Stage,
                ToLocation(diagnostic.Location));
        }

        private static HostCompilerTestLog ToLog(CompilerLogEntry log)
        {
            return new HostCompilerTestLog(
                FormatSeverity(log.Severity),
                log.Verbosity.ToString().ToLowerInvariant(),
                log.Kind.ToString().ToLowerInvariant(),
                log.Category,
                log.EventId,
                log.Message,
                log.Stage,
                log.SymbolName,
                log.Operation,
                log.Outcome.ToString().ToLowerInvariant(),
                ToLocation(log.Location),
                log.Data.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal));
        }

        private static HostCompilerTestExecution ToExecution(PassExecutionRecord execution)
        {
            return new HostCompilerTestExecution(
                execution.PassId,
                execution.Phase.ToString(),
                execution.Status.ToString(),
                ToMicroseconds(execution.Duration),
                execution.DiagnosticsAdded);
        }

        private static HostCompilerTestLocation? ToLocation(SourceLocation? location)
        {
            return location is null
                ? null
                : new HostCompilerTestLocation(
                    location.FilePath,
                    location.Line,
                    location.Column,
                    location.EndLine > 0 ? location.EndLine : null,
                    location.EndColumn > 0 ? location.EndColumn : null);
        }

        private static string FormatSeverity(DiagnosticSeverity severity)
        {
            return severity.ToString().ToLowerInvariant();
        }

        private static ArtifactRenderStatus TryRenderArtifact(
            CompilationResult result,
            string artifactName,
            out string text)
        {
            text = string.Empty;

            if (string.Equals(artifactName, CompilerArtifactKeys.LlvmIrModule.Name, StringComparison.Ordinal))
            {
                if (!result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm) || llvm is null)
                {
                    return ArtifactRenderStatus.Missing;
                }

                text = llvm.Text;
                return ArtifactRenderStatus.Rendered;
            }

            if (string.Equals(artifactName, CompilerArtifactKeys.MidLevelIr.Name, StringComparison.Ordinal))
            {
                if (!result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir) || mir is null)
                {
                    return ArtifactRenderStatus.Missing;
                }

                text = ArtifactTextRenderer.Render(mir);
                return ArtifactRenderStatus.Rendered;
            }

            if (string.Equals(artifactName, CompilerArtifactKeys.SsaIr.Name, StringComparison.Ordinal))
            {
                if (!result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa) || ssa is null)
                {
                    return ArtifactRenderStatus.Missing;
                }

                text = ArtifactTextRenderer.Render(ssa);
                return ArtifactRenderStatus.Rendered;
            }

            if (string.Equals(artifactName, CompilerArtifactKeys.OptimizedSsaIr.Name, StringComparison.Ordinal))
            {
                if (!result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa) || ssa is null)
                {
                    return ArtifactRenderStatus.Missing;
                }

                text = ArtifactTextRenderer.Render(ssa);
                return ArtifactRenderStatus.Rendered;
            }

            return result.Artifacts.Names.Contains(artifactName, StringComparer.Ordinal)
                ? ArtifactRenderStatus.Unsupported
                : ArtifactRenderStatus.Missing;
        }

        private static IReadOnlyList<HostCompilerArtifactRequest> NormalizeArtifactRequests(IReadOnlyList<string>? requestedArtifacts)
        {
            if (requestedArtifacts is null || requestedArtifacts.Count == 0)
            {
                return [];
            }

            return requestedArtifacts
                .Where(static artifact => !string.IsNullOrWhiteSpace(artifact))
                .Select(static artifact => new HostCompilerArtifactRequest(
                    artifact,
                    NormalizeArtifactName(artifact)))
                .DistinctBy(static artifact => artifact.ArtifactName)
                .ToArray();
        }

        private static string NormalizeArtifactName(string artifact)
        {
            return artifact.Trim().ToLowerInvariant() switch
            {
                "llvm" or "llvm-text" or "llvm-ir" => CompilerArtifactKeys.LlvmIrModule.Name,
                "mir" or "mir-text" => CompilerArtifactKeys.MidLevelIr.Name,
                "ssa" or "ssa-text" => CompilerArtifactKeys.SsaIr.Name,
                "optimized-ssa" or "optimized-ssa-text" or "opt-ssa" or "opt-ssa-text" => CompilerArtifactKeys.OptimizedSsaIr.Name,
                _ => artifact.Trim()
            };
        }

        private static IReadOnlyList<HostCompilerTestCompileRequest> CollectRequests(HostCompilerTestDocument document)
        {
            if (document.Requests is { Count: > 0 })
            {
                return document.Requests;
            }

            return document.Request is null ? [] : [document.Request];
        }

        private static long ToMicroseconds(TimeSpan duration)
        {
            return (duration.Ticks * 1_000_000L + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond;
        }
    }

    private enum ArtifactRenderStatus
    {
        Rendered,
        Missing,
        Unsupported
    }

    private sealed record HostCompilerArtifactRequest(string RequestedName, string ArtifactName);

    private sealed record HostCompilerTestDocument
    {
        public int ProtocolVersion { get; init; } = HostCompilerTestRunner.ProtocolVersion;
        public bool Shutdown { get; init; }
        public HostCompilerTestCompileRequest? Request { get; init; }
        public List<HostCompilerTestCompileRequest>? Requests { get; init; }
    }

    private sealed record HostCompilerTestCompileRequest
    {
        public string? Id { get; init; }
        public string? SourceText { get; init; }
        public string? SourcePath { get; init; }
        public string? FilePath { get; init; }
        public List<string>? SearchDirectories { get; init; }
        public bool? UseStarkPath { get; init; }
        public string? StopAfterPassId { get; init; }
        public bool? EmitLlvmIr { get; init; }
        public bool? ContinueAfterErrors { get; init; }
        public bool? StrictIntegerRanges { get; init; }
        public string? OptimizationLevel { get; init; }
        public string? TargetTriple { get; init; }
        public string? TargetDataLayout { get; init; }
        public string? TargetCpu { get; init; }
        public List<string>? TargetFeatures { get; init; }
        public string? RelocationModel { get; init; }
        public string? CodeModel { get; init; }
        public bool? QualifyModuleSymbols { get; init; }
        public bool? InternalizeModulePrivate { get; init; }
        public List<string>? ImportedInlineCloneSeedFunctions { get; init; }
        public int? MaximumCompileTimeLoopIterations { get; init; }
        public List<string>? Artifacts { get; init; }
        public bool? IncludeArtifactTexts { get; init; }
        public string? ArtifactOutputDirectory { get; init; }
        public string? DiagnosticsOutputPath { get; init; }
        public string? LogsOutputPath { get; init; }
        public string? ExecutionsOutputPath { get; init; }
        public bool? IncludeLogs { get; init; }
        public bool? IncludeExecutions { get; init; }
    }

    private sealed record HostCompilerTestDocumentResponse(
        int ProtocolVersion,
        bool Shutdown,
        IReadOnlyList<HostCompilerTestCompileResponse> Responses,
        string? ProtocolError)
    {
        public static HostCompilerTestDocumentResponse FromProtocolError(string error)
        {
            return new HostCompilerTestDocumentResponse(
                HostCompilerTestRunner.ProtocolVersion,
                Shutdown: false,
                Responses: [],
                ProtocolError: error);
        }

        public static HostCompilerTestDocumentResponse FromShutdown()
        {
            return new HostCompilerTestDocumentResponse(
                HostCompilerTestRunner.ProtocolVersion,
                Shutdown: true,
                Responses: [],
                ProtocolError: null);
        }
    }

    private sealed record HostCompilerTestCompileResponse(
        string? Id,
        bool Succeeded,
        string? ProtocolError,
        long DurationMicroseconds,
        HostCompilerTestDiagnosticSummary DiagnosticSummary,
        IReadOnlyList<HostCompilerTestDiagnostic> Diagnostics,
        IReadOnlyList<HostCompilerTestLog> Logs,
        IReadOnlyList<HostCompilerTestExecution> Executions,
        IReadOnlyList<string> AvailableArtifacts,
        IReadOnlyList<HostCompilerTestArtifactText> ArtifactTexts,
        IReadOnlyList<HostCompilerTestArtifactFile> ArtifactFiles,
        IReadOnlyList<string> MissingArtifacts,
        IReadOnlyList<string> UnsupportedArtifacts,
        string? DiagnosticsOutputPath,
        string? LogsOutputPath,
        string? ExecutionsOutputPath,
        IReadOnlyList<string> OutputErrors,
        string? RootModuleName,
        IReadOnlyList<string> LoadedModules)
    {
        public static HostCompilerTestCompileResponse FromProtocolError(
            string? id,
            string error,
            long durationMicroseconds)
        {
            return new HostCompilerTestCompileResponse(
                id,
                Succeeded: false,
                ProtocolError: error,
                DurationMicroseconds: durationMicroseconds,
                DiagnosticSummary: new HostCompilerTestDiagnosticSummary(0, 0, 0, 0),
                Diagnostics: [],
                Logs: [],
                Executions: [],
                AvailableArtifacts: [],
                ArtifactTexts: [],
                ArtifactFiles: [],
                MissingArtifacts: [],
                UnsupportedArtifacts: [],
                DiagnosticsOutputPath: null,
                LogsOutputPath: null,
                ExecutionsOutputPath: null,
                OutputErrors: [],
                RootModuleName: null,
                LoadedModules: []);
        }
    }

    private sealed record HostCompilerTestDiagnosticSummary(
        int TotalCount,
        int ErrorCount,
        int WarningCount,
        int InfoCount);

    private sealed record HostCompilerTestDiagnostic(
        string Code,
        string Severity,
        string Message,
        string? Stage,
        HostCompilerTestLocation? Location);

    private sealed record HostCompilerTestLog(
        string Severity,
        string Verbosity,
        string Kind,
        string Category,
        string EventId,
        string Message,
        string Stage,
        string SymbolName,
        string Operation,
        string Outcome,
        HostCompilerTestLocation? Location,
        IReadOnlyDictionary<string, string> Data);

    private sealed record HostCompilerTestExecution(
        string PassId,
        string Phase,
        string Status,
        long DurationMicroseconds,
        int DiagnosticsAdded);

    private sealed record HostCompilerTestArtifactText(
        string Name,
        string RequestedName,
        string Text);

    private sealed record HostCompilerTestArtifactFile(
        string Name,
        string RequestedName,
        string Path);

    private sealed record HostCompilerTestDiagnosticOutput(
        HostCompilerTestDiagnosticSummary Summary,
        IReadOnlyList<HostCompilerTestDiagnostic> Diagnostics);

    private sealed record HostCompilerTestLocation(
        string? FilePath,
        int Line,
        int Column,
        int? EndLine,
        int? EndColumn);
}
