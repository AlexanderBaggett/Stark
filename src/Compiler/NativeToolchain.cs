using System.Diagnostics;
using System.ComponentModel;

namespace Stark.Compiler;

internal sealed record NativeToolchainResult(
    bool Succeeded,
    string OutputPath,
    string StandardOutput,
    string StandardError)
{
    public TimeSpan Duration { get; init; } = TimeSpan.Zero;
}

internal static class NativeToolchain
{
    public static bool SupportsExecutableThinLto()
    {
        return OperatingSystem.IsWindows()
            ? CommandExists("lld-link")
            : CommandExists("ld.lld");
    }

    public static bool TryDetectDefaultTargetInfo(out LlvmTargetInfo targetInfo)
    {
        targetInfo = default!;

        try
        {
            var tempDirectory = Directory.CreateTempSubdirectory("stark-target-");
            try
            {
                var tempSourcePath = Path.Combine(tempDirectory.FullName, "empty.c");
                File.WriteAllText(tempSourcePath, string.Empty);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "clang",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("-S");
                startInfo.ArgumentList.Add("-emit-llvm");
                startInfo.ArgumentList.Add("-x");
                startInfo.ArgumentList.Add("c");
                startInfo.ArgumentList.Add(tempSourcePath);
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add("-");

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return false;
                }

                var standardOutput = process.StandardOutput.ReadToEnd();
                _ = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    return false;
                }

                string? triple = null;
                string? dataLayout = null;

                foreach (var line in standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (line.StartsWith("target triple = \"", StringComparison.Ordinal))
                    {
                        triple = ExtractQuotedValue(line);
                    }
                    else if (line.StartsWith("target datalayout = \"", StringComparison.Ordinal))
                    {
                        dataLayout = ExtractQuotedValue(line);
                    }
                }

                if (string.IsNullOrWhiteSpace(triple))
                {
                    return false;
                }

                targetInfo = new LlvmTargetInfo(triple, dataLayout);
                return true;
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
        catch
        {
            return false;
        }
    }

    public static NativeToolchainResult EmitObject(
        string llvmIr,
        string outputPath,
        string? preservedLlvmOutputPath = null,
        LlvmTargetInfo? targetInfo = null,
        CompilerOptimizationLevel optimizationLevel = CompilerOptimizationLevel.O3,
        bool enableLto = false)
    {
        return CompileLlvmIr(llvmIr, outputPath, compileOnly: true, preservedLlvmOutputPath, targetInfo, optimizationLevel, enableLto);
    }

    public static NativeToolchainResult EmitNativeObject(
        string sourcePath,
        string outputPath,
        IEnumerable<string>? includeDirectories = null,
        LlvmTargetInfo? targetInfo = null,
        CompilerOptimizationLevel optimizationLevel = CompilerOptimizationLevel.O3,
        bool enableLto = false)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = "clang",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("-ffunction-sections");
        startInfo.ArgumentList.Add("-fdata-sections");
        AppendOptimizationArgument(startInfo.ArgumentList, optimizationLevel);
        AppendCompileLtoArguments(startInfo.ArgumentList, enableLto);
        AppendTargetCodegenArguments(startInfo.ArgumentList, targetInfo, compileOnly: true);

        foreach (var includeDirectory in includeDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(includeDirectory))
            {
                continue;
            }

            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add(Path.GetFullPath(includeDirectory));
        }

        startInfo.ArgumentList.Add(fullSourcePath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(fullOutputPath);

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start clang.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        stopwatch.Stop();

        return new NativeToolchainResult(
            process.ExitCode == 0,
            fullOutputPath,
            standardOutput,
            standardError)
        { Duration = stopwatch.Elapsed };
    }

    public static NativeToolchainResult EmitExecutable(
        string llvmIr,
        string outputPath,
        LlvmTargetInfo? targetInfo = null,
        CompilerOptimizationLevel optimizationLevel = CompilerOptimizationLevel.O3)
    {
        return CompileLlvmIr(llvmIr, outputPath, compileOnly: false, preservedLlvmOutputPath: null, targetInfo, optimizationLevel, enableLto: false);
    }

    public static NativeToolchainResult LinkExecutable(
        IEnumerable<string> objectPaths,
        string outputPath,
        string? linkerTool = null,
        IEnumerable<string>? librarySearchPaths = null,
        IEnumerable<string>? extraArguments = null,
        LlvmTargetInfo? targetInfo = null,
        CompilerOptimizationLevel optimizationLevel = CompilerOptimizationLevel.O3,
        bool enableLto = false)
    {
        return RunTool(
            string.IsNullOrWhiteSpace(linkerTool) ? "clang" : linkerTool,
            BuildLinkExecutableArguments(objectPaths, outputPath, librarySearchPaths, extraArguments, targetInfo, optimizationLevel, enableLto),
            outputPath);
    }

    public static NativeToolchainResult CreateStaticLibrary(IEnumerable<string> objectPaths, string outputPath, string? archiverTool = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);

        if (!string.IsNullOrWhiteSpace(archiverTool))
        {
            var arguments = BuildStaticLibraryArguments(objectPaths, fullOutputPath);
            return RunTool(archiverTool, arguments, fullOutputPath);
        }

        var tempOutputPath = Path.Combine(
            Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory,
            $".{Guid.NewGuid():N}.{Path.GetFileName(fullOutputPath)}");

        try
        {
            var arguments = BuildStaticLibraryArguments(objectPaths, tempOutputPath);
            NativeToolchainResult result;

            if (OperatingSystem.IsWindows())
            {
                result = RunFirstAvailableTool(["llvm-lib", "lib"], arguments, tempOutputPath);
            }
            else
            {
                result = RunFirstAvailableTool(["llvm-ar", "ar"], arguments, tempOutputPath);
            }

            if (!result.Succeeded)
            {
                return result with { OutputPath = fullOutputPath };
            }

            if (File.Exists(fullOutputPath))
            {
                File.Delete(fullOutputPath);
            }

            File.Move(tempOutputPath, fullOutputPath);
            return result with { OutputPath = fullOutputPath };
        }
        finally
        {
            try
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static NativeToolchainResult CompileLlvmIr(
        string llvmIr,
        string outputPath,
        bool compileOnly,
        string? preservedLlvmOutputPath,
        LlvmTargetInfo? targetInfo,
        CompilerOptimizationLevel optimizationLevel,
        bool enableLto)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);

        DirectoryInfo? tempDirectory = null;
        try
        {
            var llvmPath = string.IsNullOrWhiteSpace(preservedLlvmOutputPath)
                ? Path.Combine((tempDirectory = Directory.CreateTempSubdirectory("stark-llvm-")).FullName, "module.ll")
                : Path.GetFullPath(preservedLlvmOutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(llvmPath) ?? Environment.CurrentDirectory);
            File.WriteAllText(llvmPath, llvmIr);

            var startInfo = new ProcessStartInfo
            {
                FileName = "clang",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-Wno-override-module");
            if (compileOnly)
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("-ffunction-sections");
                startInfo.ArgumentList.Add("-fdata-sections");
            }

            AppendOptimizationArgument(startInfo.ArgumentList, optimizationLevel);
            AppendCompileLtoArguments(startInfo.ArgumentList, compileOnly && enableLto);
            AppendStarkLlvmIrCompileStabilityArguments(startInfo.ArgumentList, optimizationLevel, compileOnly && enableLto);
            AppendTargetCodegenArguments(startInfo.ArgumentList, targetInfo, compileOnly);
            startInfo.ArgumentList.Add(llvmPath);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(fullOutputPath);

            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start clang.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            stopwatch.Stop();

            return new NativeToolchainResult(
                process.ExitCode == 0,
                fullOutputPath,
                standardOutput,
                standardError)
            { Duration = stopwatch.Elapsed };
        }
        finally
        {
            try
            {
                tempDirectory?.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static IEnumerable<string> BuildLinkExecutableArguments(
        IEnumerable<string> objectPaths,
        string outputPath,
        IEnumerable<string>? librarySearchPaths,
        IEnumerable<string>? extraArguments,
        LlvmTargetInfo? targetInfo,
        CompilerOptimizationLevel optimizationLevel,
        bool enableLto)
    {
        if (targetInfo is not null && !string.IsNullOrWhiteSpace(targetInfo.Triple))
        {
            yield return "-target";
            yield return targetInfo.Triple;
        }

        if (enableLto)
        {
            yield return "-flto=thin";
            yield return optimizationLevel switch
            {
                CompilerOptimizationLevel.O0 => "-O0",
                CompilerOptimizationLevel.Og => "-Og",
                CompilerOptimizationLevel.O1 => "-O1",
                CompilerOptimizationLevel.O2 => "-O2",
                _ => "-O3"
            };

            if ((OperatingSystem.IsWindows() && CommandExists("lld-link"))
                || (!OperatingSystem.IsWindows() && CommandExists("ld.lld")))
            {
                yield return "-fuse-ld=lld";
            }
        }

        foreach (var objectPath in objectPaths)
        {
            yield return Path.GetFullPath(objectPath);
        }

        if (librarySearchPaths is not null)
        {
            foreach (var searchPath in librarySearchPaths)
            {
                yield return "-L";
                yield return Path.GetFullPath(searchPath);
            }
        }

        if (extraArguments is not null)
        {
            foreach (var argument in extraArguments)
            {
                yield return argument;
            }
        }

        foreach (var argument in GetRelocationLinkArguments(targetInfo))
        {
            yield return argument;
        }

        yield return "-o";
        yield return Path.GetFullPath(outputPath);
    }

    private static IEnumerable<string> BuildStaticLibraryArguments(IEnumerable<string> objectPaths, string outputPath)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return $"/OUT:{Path.GetFullPath(outputPath)}";
        }
        else
        {
            yield return "rcs";
            yield return Path.GetFullPath(outputPath);
        }

        foreach (var objectPath in objectPaths)
        {
            yield return Path.GetFullPath(objectPath);
        }
    }

    private static NativeToolchainResult RunFirstAvailableTool(IEnumerable<string> toolNames, IEnumerable<string> arguments, string outputPath)
    {
        NativeToolchainResult? lastFailure = null;

        foreach (var toolName in toolNames)
        {
            try
            {
                return RunTool(toolName, arguments, outputPath);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                lastFailure = new NativeToolchainResult(
                    Succeeded: false,
                    OutputPath: Path.GetFullPath(outputPath),
                    StandardOutput: string.Empty,
                    StandardError: exception.Message);
            }
        }

        return lastFailure ?? new NativeToolchainResult(
            Succeeded: false,
            OutputPath: Path.GetFullPath(outputPath),
            StandardOutput: string.Empty,
            StandardError: "No suitable native tool was available.");
    }

    private static NativeToolchainResult RunTool(string toolName, IEnumerable<string> arguments, string outputPath)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = toolName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {toolName}.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        stopwatch.Stop();

        return new NativeToolchainResult(
            process.ExitCode == 0,
            fullOutputPath,
            standardOutput,
            standardError)
        { Duration = stopwatch.Elapsed };
    }

    private static string? ExtractQuotedValue(string line)
    {
        var firstQuote = line.IndexOf('"');
        var lastQuote = line.LastIndexOf('"');
        return firstQuote >= 0 && lastQuote > firstQuote
            ? line[(firstQuote + 1)..lastQuote]
            : null;
    }

    private static void AppendTargetCodegenArguments(ICollection<string> arguments, LlvmTargetInfo? targetInfo, bool compileOnly)
    {
        if (targetInfo is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(targetInfo.Triple))
        {
            arguments.Add("-target");
            arguments.Add(targetInfo.Triple);
        }

        if (!string.IsNullOrWhiteSpace(targetInfo.Cpu))
        {
            arguments.Add($"-mcpu={targetInfo.Cpu}");
        }

        foreach (var feature in targetInfo.Features ?? [])
        {
            if (string.IsNullOrWhiteSpace(feature))
            {
                continue;
            }

            arguments.Add("-Xclang");
            arguments.Add("-target-feature");
            arguments.Add("-Xclang");
            arguments.Add(feature);
        }

        AppendCodegenModelArguments(arguments, targetInfo, compileOnly);
    }

    private static void AppendCodegenModelArguments(ICollection<string> arguments, LlvmTargetInfo targetInfo, bool compileOnly)
    {
        switch (targetInfo.RelocationModel)
        {
            case LlvmRelocationModel.Static:
                arguments.Add("-fno-pic");
                arguments.Add("-fno-pie");
                if (!compileOnly && !OperatingSystem.IsWindows())
                {
                    arguments.Add("-no-pie");
                }

                break;
            case LlvmRelocationModel.Pic:
                arguments.Add("-fPIC");
                break;
            case LlvmRelocationModel.Pie:
                arguments.Add("-fPIE");
                if (!compileOnly && !OperatingSystem.IsWindows())
                {
                    arguments.Add("-pie");
                }

                break;
        }

        if (targetInfo.CodeModel is not null)
        {
            arguments.Add($"-mcmodel={FormatCodeModel(targetInfo.CodeModel.Value)}");
        }
    }

    private static IEnumerable<string> GetRelocationLinkArguments(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo is null || OperatingSystem.IsWindows())
        {
            yield break;
        }

        switch (targetInfo.RelocationModel)
        {
            case LlvmRelocationModel.Static:
                yield return "-no-pie";
                yield break;
            case LlvmRelocationModel.Pie:
                yield return "-pie";
                yield break;
        }
    }

    private static string FormatCodeModel(LlvmCodeModel codeModel)
    {
        return codeModel switch
        {
            LlvmCodeModel.Tiny => "tiny",
            LlvmCodeModel.Small => "small",
            LlvmCodeModel.Kernel => "kernel",
            LlvmCodeModel.Medium => "medium",
            LlvmCodeModel.Large => "large",
            _ => throw new InvalidOperationException($"Unsupported code model '{codeModel}'.")
        };
    }

    private static void AppendOptimizationArgument(ICollection<string> arguments, CompilerOptimizationLevel optimizationLevel)
    {
        arguments.Add(optimizationLevel switch
        {
            CompilerOptimizationLevel.O0 => "-O0",
            CompilerOptimizationLevel.Og => "-Og",
            CompilerOptimizationLevel.O1 => "-O1",
            CompilerOptimizationLevel.O2 => "-O2",
            _ => "-O3"
        });
    }

    private static void AppendStarkLlvmIrCompileStabilityArguments(
        ICollection<string> arguments,
        CompilerOptimizationLevel optimizationLevel,
        bool enableLto)
    {
        if (optimizationLevel == CompilerOptimizationLevel.O0 || enableLto)
        {
            return;
        }

        // Stark runs its own high-level optimization pipeline before LLVM emission.
        // Let clang lower the IR to native code, but avoid known pathological LLVM
        // optimizer behavior on generated stdlib modules such as System.FileSystem.
        // ThinLTO bitcode is the exception: the link-time optimizer expects
        // ordinary optimized module summaries, and disabling compile-time LLVM
        // passes there can produce incorrect cross-module optimization.
        arguments.Add("-Xclang");
        arguments.Add("-disable-llvm-passes");
    }

    private static void AppendCompileLtoArguments(ICollection<string> arguments, bool enableLto)
    {
        if (enableLto)
        {
            arguments.Add("-flto=thin");
        }
    }

    private static bool CommandExists(string commandName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var commandCandidates = BuildCommandCandidates(commandName);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var commandCandidate in commandCandidates)
            {
                var candidate = Path.Combine(directory, commandCandidate);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildCommandCandidates(string commandName)
    {
        if (Path.HasExtension(commandName))
        {
            return [commandName];
        }

        if (!OperatingSystem.IsWindows())
        {
            return [commandName];
        }

        var candidates = new List<string> { commandName };
        var extensions = Environment.GetEnvironmentVariable("PATHEXT")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [".COM", ".EXE", ".BAT", ".CMD"];

        foreach (var extension in extensions)
        {
            candidates.Add($"{commandName}{extension}");
        }

        return candidates;
    }
}
