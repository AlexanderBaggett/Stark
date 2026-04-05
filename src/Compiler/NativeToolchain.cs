using System.Diagnostics;
using System.ComponentModel;

namespace Stark.Compiler;

internal sealed record NativeToolchainResult(
    bool Succeeded,
    string OutputPath,
    string StandardOutput,
    string StandardError);

internal static class NativeToolchain
{
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
        LlvmTargetInfo? targetInfo = null)
    {
        return CompileLlvmIr(llvmIr, outputPath, compileOnly: true, preservedLlvmOutputPath, targetInfo);
    }

    public static NativeToolchainResult EmitExecutable(string llvmIr, string outputPath, LlvmTargetInfo? targetInfo = null)
    {
        return CompileLlvmIr(llvmIr, outputPath, compileOnly: false, preservedLlvmOutputPath: null, targetInfo);
    }

    public static NativeToolchainResult LinkExecutable(
        IEnumerable<string> objectPaths,
        string outputPath,
        string? linkerTool = null,
        IEnumerable<string>? librarySearchPaths = null,
        IEnumerable<string>? extraArguments = null)
    {
        return RunTool(
            string.IsNullOrWhiteSpace(linkerTool) ? "clang" : linkerTool,
            BuildLinkExecutableArguments(objectPaths, outputPath, librarySearchPaths, extraArguments),
            outputPath);
    }

    public static NativeToolchainResult CreateStaticLibrary(IEnumerable<string> objectPaths, string outputPath, string? archiverTool = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);

        var tempOutputPath = Path.Combine(
            Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory,
            $".{Guid.NewGuid():N}.{Path.GetFileName(fullOutputPath)}");

        try
        {
            var arguments = BuildStaticLibraryArguments(objectPaths, tempOutputPath);
            NativeToolchainResult result;

            if (!string.IsNullOrWhiteSpace(archiverTool))
            {
                result = RunTool(archiverTool, arguments, tempOutputPath);
            }
            else if (OperatingSystem.IsWindows())
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
        LlvmTargetInfo? targetInfo)
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
            }

            AppendTargetCodegenArguments(startInfo.ArgumentList, targetInfo);
            startInfo.ArgumentList.Add(llvmPath);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(fullOutputPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start clang.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new NativeToolchainResult(
                process.ExitCode == 0,
                fullOutputPath,
                standardOutput,
                standardError);
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
        IEnumerable<string>? extraArguments)
    {
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {toolName}.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new NativeToolchainResult(
            process.ExitCode == 0,
            fullOutputPath,
            standardOutput,
            standardError);
    }

    private static string? ExtractQuotedValue(string line)
    {
        var firstQuote = line.IndexOf('"');
        var lastQuote = line.LastIndexOf('"');
        return firstQuote >= 0 && lastQuote > firstQuote
            ? line[(firstQuote + 1)..lastQuote]
            : null;
    }

    private static void AppendTargetCodegenArguments(ICollection<string> arguments, LlvmTargetInfo? targetInfo)
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

        if (targetInfo.Features is null)
        {
            return;
        }

        foreach (var feature in targetInfo.Features)
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
    }
}
