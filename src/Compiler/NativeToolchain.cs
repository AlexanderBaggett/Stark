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

internal enum NativeToolchainResolutionSource
{
    Missing,
    CliOverride,
    EnvironmentOverride,
    UserConfig,
    Bundled,
    Path
}

internal sealed record NativeToolchainResolutionOptions(
    string? CliToolchainDirectory = null,
    string? UserConfigToolchainDirectory = null,
    string? ClangTool = null,
    string? LinkerTool = null,
    string? ArchiverTool = null,
    string? LlvmLibraryPath = null,
    string? SdkRootDirectory = null,
    bool AllowAmbientCompilerBackendFallback = true);

internal sealed record NativeResolvedTool(
    string Role,
    string RequestedName,
    string? Path,
    NativeToolchainResolutionSource Source)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Path);

    public string CommandPathOrName => Path ?? RequestedName;
}

internal sealed record NativeResolvedFile(
    string Role,
    string RequestedName,
    string? Path,
    NativeToolchainResolutionSource Source)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Path);
}

internal sealed record NativeToolchainResolution(
    NativeResolvedTool Clang,
    NativeResolvedTool Linker,
    NativeResolvedTool Archiver,
    NativeResolvedTool Lld,
    NativeResolvedTool PkgConfig,
    NativeResolvedTool Xcrun,
    NativeResolvedFile LlvmLibrary,
    string? MacOSSdkRoot,
    IReadOnlyList<string> SearchRoots);

internal sealed record NativeToolchainSearchRoot(
    string Path,
    NativeToolchainResolutionSource Source);

internal static class NativeToolchain
{
    public static NativeToolchainResolution Resolve(NativeToolchainResolutionOptions? options = null)
    {
        options ??= new NativeToolchainResolutionOptions();
        var searchRoots = BuildSearchRoots(options).ToArray();
        var clang = ResolveTool(
            "clang",
            ["clang"],
            options.ClangTool,
            "STARK_CLANG",
            searchRoots,
            allowPathFallback: options.AllowAmbientCompilerBackendFallback);
        var linker = ResolveTool(
            "linker",
            ["clang"],
            options.LinkerTool,
            "STARK_LINKER",
            searchRoots);
        var archiver = ResolveTool(
            "archiver",
            OperatingSystem.IsWindows() ? ["llvm-lib", "lib"] : ["llvm-ar", "ar"],
            options.ArchiverTool,
            "STARK_ARCHIVER",
            searchRoots,
            allowPathFallback: options.AllowAmbientCompilerBackendFallback);
        var lld = ResolveTool(
            "lld",
            OperatingSystem.IsWindows() ? ["lld-link"] : ["ld.lld"],
            explicitOverride: null,
            environmentVariableName: null,
            searchRoots,
            allowPathFallback: options.AllowAmbientCompilerBackendFallback);
        var pkgConfig = ResolveTool(
            "pkg-config",
            ["pkg-config"],
            explicitOverride: null,
            environmentVariableName: "STARK_PKG_CONFIG",
            searchRoots);
        var xcrun = ResolveTool(
            "xcrun",
            ["xcrun"],
            explicitOverride: null,
            environmentVariableName: null,
            searchRoots);
        // PATH isolation is a release qualification tool, but xcrun is an OS
        // SDK locator supplied by macOS rather than a redistributable LLVM
        // tool. Keep the well-known host path available without adding
        // /usr/bin to PATH, which could otherwise hide a missing bundled
        // clang, linker, or archiver during archive smoke tests.
        if (OperatingSystem.IsMacOS()
            && !xcrun.IsAvailable
            && File.Exists("/usr/bin/xcrun"))
        {
            xcrun = new NativeResolvedTool(
                "xcrun",
                "xcrun",
                "/usr/bin/xcrun",
                NativeToolchainResolutionSource.Path);
        }
        var llvmLibrary = ResolveLlvmLibrary(options, searchRoots);
        var sdkRoot = ResolveMacOSSdkRoot(xcrun.Path);

        return new NativeToolchainResolution(
            clang,
            linker,
            archiver,
            lld,
            pkgConfig,
            xcrun,
            llvmLibrary,
            sdkRoot,
            searchRoots.Select(static root => root.Path).Distinct(StringComparer.Ordinal).ToArray());
    }

    public static bool SupportsExecutableThinLto(NativeToolchainResolution? toolchain = null)
    {
        var resolvedToolchain = toolchain ?? Resolve();
        return resolvedToolchain.Lld.IsAvailable;
    }

    public static bool ShouldUseMacOSPlatformSdkForTarget(LlvmTargetInfo? targetInfo) => ShouldUseMacOSPlatformSdk(targetInfo);

    public static bool TryResolveMacOSSdkRoot(out string sdkRoot, NativeToolchainResolution? toolchain = null)
    {
        sdkRoot = (toolchain?.MacOSSdkRoot ?? ResolveMacOSSdkRoot()) ?? string.Empty;
        return sdkRoot.Length != 0;
    }

    public static bool TryDetectDefaultTargetInfo(out LlvmTargetInfo targetInfo, NativeToolchainResolution? toolchain = null)
    {
        return TryDetectTargetInfoCore(targetTriple: null, out targetInfo, toolchain);
    }

    public static bool TryDetectTargetInfo(
        string targetTriple,
        out LlvmTargetInfo targetInfo,
        NativeToolchainResolution? toolchain = null)
    {
        if (string.IsNullOrWhiteSpace(targetTriple))
        {
            targetInfo = default!;
            return false;
        }

        return TryDetectTargetInfoCore(targetTriple.Trim(), out targetInfo, toolchain);
    }

    private static bool TryDetectTargetInfoCore(
        string? targetTriple,
        out LlvmTargetInfo targetInfo,
        NativeToolchainResolution? toolchain)
    {
        targetInfo = default!;
        var resolvedToolchain = toolchain ?? Resolve();
        if (!resolvedToolchain.Clang.IsAvailable)
        {
            return false;
        }

        try
        {
            var tempDirectory = Directory.CreateTempSubdirectory("stark-target-");
            try
            {
                var tempSourcePath = Path.Combine(tempDirectory.FullName, "empty.c");
                File.WriteAllText(tempSourcePath, string.Empty);

                var startInfo = new ProcessStartInfo
                {
                    FileName = resolvedToolchain.Clang.Path!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("-S");
                startInfo.ArgumentList.Add("-emit-llvm");
                startInfo.ArgumentList.Add("-x");
                startInfo.ArgumentList.Add("c");
                if (targetTriple is not null)
                {
                    startInfo.ArgumentList.Add($"--target={targetTriple}");
                }

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

                targetInfo = new LlvmTargetInfo(triple, NormalizeDetectedDataLayout(dataLayout, triple));
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
        bool enableLto = false,
        NativeToolchainResolution? toolchain = null)
    {
        return CompileLlvmIr(llvmIr, outputPath, compileOnly: true, preservedLlvmOutputPath, targetInfo, enableLto, toolchain);
    }

    public static NativeToolchainResult EmitNativeObject(
        string sourcePath,
        string outputPath,
        IEnumerable<string>? includeDirectories = null,
        LlvmTargetInfo? targetInfo = null,
        bool enableLto = false,
        NativeToolchainResolution? toolchain = null)
    {
        var resolvedToolchain = toolchain ?? Resolve();
        if (!resolvedToolchain.Clang.IsAvailable)
        {
            return MissingToolResult(resolvedToolchain.Clang, outputPath);
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedToolchain.Clang.Path!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("-ffunction-sections");
        startInfo.ArgumentList.Add("-fdata-sections");
        startInfo.ArgumentList.Add("-O3");
        AppendCompileLtoArguments(startInfo.ArgumentList, enableLto);
        AppendTargetCodegenArguments(startInfo.ArgumentList, targetInfo, compileOnly: true);
        AppendMacOSPlatformSdkArguments(startInfo.ArgumentList, targetInfo, forClangDriver: true, resolvedToolchain);

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
        LlvmTargetInfo? targetInfo = null)
    {
        return CompileLlvmIr(llvmIr, outputPath, compileOnly: false, preservedLlvmOutputPath: null, targetInfo, enableLto: false, toolchain: null);
    }

    public static NativeToolchainResult LinkExecutable(
        IEnumerable<string> objectPaths,
        string outputPath,
        string? linkerTool = null,
        IEnumerable<string>? librarySearchPaths = null,
        IEnumerable<string>? extraArguments = null,
        LlvmTargetInfo? targetInfo = null,
        bool enableLto = false,
        NativeToolchainResolution? toolchain = null)
    {
        var resolvedToolchain = toolchain ?? Resolve(new NativeToolchainResolutionOptions(LinkerTool: linkerTool));
        if (!resolvedToolchain.Linker.IsAvailable)
        {
            return MissingToolResult(resolvedToolchain.Linker, outputPath);
        }

        var resolvedLinkerTool = resolvedToolchain.Linker.Path!;
        return RunTool(
            resolvedLinkerTool,
            BuildLinkExecutableArguments(
                objectPaths,
                outputPath,
                librarySearchPaths,
                extraArguments,
                targetInfo,
                enableLto,
                IsClangDriver(resolvedLinkerTool),
                resolvedToolchain.Lld.IsAvailable,
                resolvedToolchain.MacOSSdkRoot),
            outputPath);
    }

    public static NativeToolchainResult CreateStaticLibrary(
        IEnumerable<string> objectPaths,
        string outputPath,
        string? archiverTool = null,
        NativeToolchainResolution? toolchain = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory);
        var resolvedToolchain = toolchain ?? Resolve(new NativeToolchainResolutionOptions(ArchiverTool: archiverTool));
        if (!resolvedToolchain.Archiver.IsAvailable)
        {
            return MissingToolResult(resolvedToolchain.Archiver, fullOutputPath);
        }

        var tempOutputPath = Path.Combine(
            Path.GetDirectoryName(fullOutputPath) ?? Environment.CurrentDirectory,
            $".{Guid.NewGuid():N}.{Path.GetFileName(fullOutputPath)}");

        try
        {
            var arguments = BuildStaticLibraryArguments(objectPaths, tempOutputPath);
            var result = SuppressHarmlessStaticLibraryWarnings(RunTool(resolvedToolchain.Archiver.Path!, arguments, tempOutputPath));

            if (!result.Succeeded)
            {
                return result with { OutputPath = fullOutputPath };
            }

            File.Move(tempOutputPath, fullOutputPath, overwrite: true);
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

    private static IEnumerable<NativeToolchainSearchRoot> BuildSearchRoots(NativeToolchainResolutionOptions options)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in EnumerateSearchRootCandidates(options.CliToolchainDirectory, NativeToolchainResolutionSource.CliOverride))
        {
            if (seen.Add(root.Path))
            {
                yield return root;
            }
        }

        foreach (var root in EnumerateSearchRootCandidates(Environment.GetEnvironmentVariable("STARK_TOOLCHAIN_DIR"), NativeToolchainResolutionSource.EnvironmentOverride))
        {
            if (seen.Add(root.Path))
            {
                yield return root;
            }
        }

        foreach (var root in EnumerateSearchRootCandidates(options.UserConfigToolchainDirectory, NativeToolchainResolutionSource.UserConfig))
        {
            if (seen.Add(root.Path))
            {
                yield return root;
            }
        }

        foreach (var root in EnumerateBundledSearchRoots(options.SdkRootDirectory))
        {
            if (seen.Add(root.Path))
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<NativeToolchainSearchRoot> EnumerateBundledSearchRoots(
        string? selectedSdkRootDirectory)
    {
        var sdkRootDirectory = ResolveBundledSdkRoot(selectedSdkRootDirectory);
        if (!string.IsNullOrWhiteSpace(sdkRootDirectory))
        {
            foreach (var root in EnumerateSearchRootCandidates(
                         Path.Combine(sdkRootDirectory, "toolchain"),
                         NativeToolchainResolutionSource.Bundled))
            {
                yield return root;
            }

            foreach (var root in EnumerateSearchRootCandidates(
                         Path.Combine(sdkRootDirectory, "toolchain", "llvm-22.1.8"),
                         NativeToolchainResolutionSource.Bundled))
            {
                yield return root;
            }
        }

        // Retain the original executable-directory lookup for repository
        // launchers and `dotnet compiler.dll` development flows. Installed
        // archives use <sdk>/bin/stark and are handled through the canonical
        // SDK root above, so their toolchain remains a sibling of bin/.
        var compilerDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(compilerDirectory))
        {
            yield break;
        }

        foreach (var root in EnumerateSearchRootCandidates(
                     Path.Combine(compilerDirectory, "toolchain"),
                     NativeToolchainResolutionSource.Bundled))
        {
            yield return root;
        }

        foreach (var root in EnumerateSearchRootCandidates(
                     Path.Combine(compilerDirectory, "toolchain", "llvm-22.1.8"),
                     NativeToolchainResolutionSource.Bundled))
        {
            yield return root;
        }
    }

    private static string? ResolveBundledSdkRoot(string? selectedSdkRootDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(selectedSdkRootDirectory))
            {
                return SdkRootResolver.Resolve(explicitRoot: selectedSdkRootDirectory).RootPath;
            }

            var discovered = SdkRootResolver.Resolve();
            return File.Exists(discovered.ManifestPath) ? discovered.RootPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            // Missing SDK discovery must not hide explicit toolchain overrides
            // or the ordinary PATH fallback. The SDK resolver owns its own
            // user-facing diagnostics when an SDK is actually required.
            return null;
        }
    }

    private static IEnumerable<NativeToolchainSearchRoot> EnumerateSearchRootCandidates(
        string? configuredPath,
        NativeToolchainResolutionSource source)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            yield break;
        }

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
        if (!Directory.Exists(fullPath))
        {
            yield break;
        }

        if (Directory.Exists(Path.Combine(fullPath, "bin"))
            || Directory.Exists(Path.Combine(fullPath, "lib")))
        {
            yield return new NativeToolchainSearchRoot(fullPath, source);
        }

        foreach (var childRoot in EnumerateExistingToolchainChildren(fullPath))
        {
            yield return new NativeToolchainSearchRoot(childRoot, source);
        }
    }

    private static IEnumerable<string> EnumerateExistingToolchainChildren(string rootPath)
    {
        foreach (var parent in new[]
                 {
                     rootPath,
                     Path.Combine(rootPath, "toolchain")
                 })
        {
            if (!Directory.Exists(parent))
            {
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(parent)
                         .Where(static path => Path.GetFileName(path).StartsWith("llvm-", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                if (Directory.Exists(Path.Combine(child, "bin"))
                    || Directory.Exists(Path.Combine(child, "lib")))
                {
                    yield return Path.GetFullPath(child);
                }
            }
        }
    }

    private static NativeResolvedTool ResolveTool(
        string role,
        IReadOnlyList<string> defaultNames,
        string? explicitOverride,
        string? environmentVariableName,
        IReadOnlyList<NativeToolchainSearchRoot> searchRoots,
        bool allowPathFallback = true)
    {
        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            var requestedName = explicitOverride.Trim();
            return new NativeResolvedTool(
                role,
                requestedName,
                FindExecutable(requestedName),
                NativeToolchainResolutionSource.CliOverride);
        }

        if (!string.IsNullOrWhiteSpace(environmentVariableName)
            && Environment.GetEnvironmentVariable(environmentVariableName) is { } environmentOverride
            && !string.IsNullOrWhiteSpace(environmentOverride))
        {
            var requestedName = environmentOverride.Trim();
            return new NativeResolvedTool(
                role,
                requestedName,
                FindExecutable(requestedName),
                NativeToolchainResolutionSource.EnvironmentOverride);
        }

        foreach (var root in searchRoots)
        {
            foreach (var defaultName in defaultNames)
            {
                if (TryFindToolInRoot(root.Path, defaultName, out var toolPath))
                {
                    return new NativeResolvedTool(role, defaultName, toolPath, root.Source);
                }
            }
        }

        if (allowPathFallback)
        {
            foreach (var defaultName in defaultNames)
            {
                if (FindExecutable(defaultName) is { } path)
                {
                    return new NativeResolvedTool(role, defaultName, path, NativeToolchainResolutionSource.Path);
                }
            }
        }

        return new NativeResolvedTool(role, defaultNames[0], null, NativeToolchainResolutionSource.Missing);
    }

    private static NativeResolvedFile ResolveLlvmLibrary(
        NativeToolchainResolutionOptions options,
        IReadOnlyList<NativeToolchainSearchRoot> searchRoots)
    {
        if (!string.IsNullOrWhiteSpace(options.LlvmLibraryPath))
        {
            var requestedPath = options.LlvmLibraryPath.Trim();
            return new NativeResolvedFile(
                "libLLVM",
                requestedPath,
                File.Exists(requestedPath) ? Path.GetFullPath(requestedPath) : null,
                NativeToolchainResolutionSource.CliOverride);
        }

        if (Environment.GetEnvironmentVariable("STARK_LLVM_LIB") is { } environmentPath
            && !string.IsNullOrWhiteSpace(environmentPath))
        {
            var requestedPath = environmentPath.Trim();
            return new NativeResolvedFile(
                "libLLVM",
                requestedPath,
                File.Exists(requestedPath) ? Path.GetFullPath(requestedPath) : null,
                NativeToolchainResolutionSource.EnvironmentOverride);
        }

        foreach (var root in searchRoots)
        {
            if (TryFindLlvmLibraryInRoot(root.Path, out var libraryPath))
            {
                return new NativeResolvedFile("libLLVM", Path.GetFileName(libraryPath), libraryPath, root.Source);
            }
        }

        return new NativeResolvedFile("libLLVM", "libLLVM", null, NativeToolchainResolutionSource.Missing);
    }

    private static bool TryFindToolInRoot(string rootPath, string toolName, out string toolPath)
    {
        foreach (var candidateName in BuildCommandCandidates(toolName))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(rootPath, "bin", candidateName),
                         Path.Combine(rootPath, candidateName)
                     })
            {
                if (File.Exists(candidate))
                {
                    toolPath = Path.GetFullPath(candidate);
                    return true;
                }
            }
        }

        toolPath = string.Empty;
        return false;
    }

    private static bool TryFindLlvmLibraryInRoot(string rootPath, out string libraryPath)
    {
        foreach (var relativePath in GetLlvmLibraryRelativeCandidates())
        {
            var candidate = Path.Combine(rootPath, relativePath);
            if (File.Exists(candidate))
            {
                libraryPath = Path.GetFullPath(candidate);
                return true;
            }
        }

        foreach (var pattern in GetLlvmLibrarySearchPatterns())
        {
            var directory = Path.Combine(rootPath, pattern.DirectoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var candidate = Directory.EnumerateFiles(directory, pattern.FilePattern)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is not null)
            {
                libraryPath = Path.GetFullPath(candidate);
                return true;
            }
        }

        libraryPath = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> GetLlvmLibraryRelativeCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine("bin", "LLVM-C.dll"),
                Path.Combine("bin", "LLVM.dll"),
                Path.Combine("lib", "LLVM-C.lib"),
                Path.Combine("lib", "LLVM.lib")
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                Path.Combine("lib", "libLLVM.dylib"),
                Path.Combine("lib", "libLLVM-C.dylib")
            ];
        }

        return
        [
            Path.Combine("lib", "libLLVM.so"),
            Path.Combine("lib", "libLLVM-22.1.so"),
            Path.Combine("lib", "libLLVM-22.so")
        ];
    }

    private static IReadOnlyList<(string DirectoryName, string FilePattern)> GetLlvmLibrarySearchPatterns()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                ("bin", "LLVM*.dll"),
                ("lib", "LLVM*.lib")
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return [("lib", "libLLVM*.dylib")];
        }

        return [("lib", "libLLVM*.so*")];
    }

    internal static string? FindExecutable(string commandName)
    {
        if (Path.IsPathRooted(commandName)
            || commandName.Contains(Path.DirectorySeparatorChar)
            || commandName.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(commandName) ? Path.GetFullPath(commandName) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var commandCandidates = BuildCommandCandidates(commandName);
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var commandCandidate in commandCandidates)
            {
                var candidate = Path.Combine(directory, commandCandidate);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static NativeToolchainResult MissingToolResult(NativeResolvedTool tool, string outputPath)
    {
        var requested = string.IsNullOrWhiteSpace(tool.RequestedName) ? tool.Role : tool.RequestedName;
        return new NativeToolchainResult(
            Succeeded: false,
            OutputPath: Path.GetFullPath(outputPath),
            StandardOutput: string.Empty,
            StandardError: $"{tool.Role} tool '{requested}' was not found.");
    }

    private static NativeToolchainResult SuppressHarmlessStaticLibraryWarnings(NativeToolchainResult result)
    {
        if (!result.Succeeded || string.IsNullOrEmpty(result.StandardError))
        {
            return result;
        }

        var retainedLines = result.StandardError
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(static line => line.Length != 0 && !IsHarmlessStaticLibraryWarning(line))
            .ToArray();
        return result with { StandardError = string.Join(Environment.NewLine, retainedLines) };
    }

    private static bool IsHarmlessStaticLibraryWarning(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("ranlib: warning: '", StringComparison.Ordinal)
            && trimmed.EndsWith("' has no symbols", StringComparison.Ordinal);
    }

    private static NativeToolchainResult CompileLlvmIr(
        string llvmIr,
        string outputPath,
        bool compileOnly,
        string? preservedLlvmOutputPath,
        LlvmTargetInfo? targetInfo,
        bool enableLto,
        NativeToolchainResolution? toolchain)
    {
        var resolvedToolchain = toolchain ?? Resolve();
        if (!resolvedToolchain.Clang.IsAvailable)
        {
            return MissingToolResult(resolvedToolchain.Clang, outputPath);
        }

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
                FileName = resolvedToolchain.Clang.Path!,
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

            startInfo.ArgumentList.Add("-O3");
            AppendCompileLtoArguments(startInfo.ArgumentList, compileOnly && enableLto);
            AppendStarkLlvmIrCompileStabilityArguments(startInfo.ArgumentList, llvmIr, compileOnly && enableLto);
            AppendTargetCodegenArguments(startInfo.ArgumentList, targetInfo, compileOnly);
            AppendMacOSPlatformSdkArguments(startInfo.ArgumentList, targetInfo, forClangDriver: true, resolvedToolchain);
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
        bool enableLto,
        bool linkerIsClangDriver,
        bool lldAvailable,
        string? macOSSdkRoot)
    {
        if (targetInfo is not null && !string.IsNullOrWhiteSpace(targetInfo.Triple))
        {
            yield return "-target";
            yield return targetInfo.Triple;
        }

        foreach (var argument in GetMacOSPlatformSdkArguments(targetInfo, linkerIsClangDriver, macOSSdkRoot))
        {
            yield return argument;
        }

        if (enableLto)
        {
            yield return "-flto=thin";
            yield return "-O3";

            if (lldAvailable)
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

    private static string? NormalizeDetectedDataLayout(string? dataLayout, string triple)
    {
        if (string.IsNullOrWhiteSpace(dataLayout)
            || HasDefaultPointerLayout(dataLayout)
            || !TryInferDefaultPointerBits(triple, out var pointerBits))
        {
            return dataLayout;
        }

        var tokens = dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tokens.Count == 0)
        {
            return dataLayout;
        }

        var insertIndex = tokens.Count > 1 && tokens[1].StartsWith("m:", StringComparison.Ordinal) ? 2 : 1;
        tokens.Insert(insertIndex, $"p:{pointerBits}:{pointerBits}");
        return string.Join("-", tokens);
    }

    private static bool HasDefaultPointerLayout(string dataLayout)
    {
        foreach (var token in dataLayout.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("p:", StringComparison.Ordinal)
                || token.StartsWith("p0:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInferDefaultPointerBits(string triple, out int pointerBits)
    {
        var lower = triple.Trim().ToLowerInvariant();
        if (lower.StartsWith("i386-", StringComparison.Ordinal)
            || lower.StartsWith("i486-", StringComparison.Ordinal)
            || lower.StartsWith("i586-", StringComparison.Ordinal)
            || lower.StartsWith("i686-", StringComparison.Ordinal)
            || lower.StartsWith("x86-", StringComparison.Ordinal)
            || lower.StartsWith("armv7", StringComparison.Ordinal)
            || lower.StartsWith("armv6", StringComparison.Ordinal)
            || lower.StartsWith("wasm32-", StringComparison.Ordinal))
        {
            pointerBits = 32;
            return true;
        }

        if (lower.StartsWith("x86_64-", StringComparison.Ordinal)
            || lower.StartsWith("amd64-", StringComparison.Ordinal)
            || lower.StartsWith("aarch64-", StringComparison.Ordinal)
            || lower.StartsWith("arm64-", StringComparison.Ordinal)
            || lower.StartsWith("riscv64-", StringComparison.Ordinal)
            || lower.StartsWith("wasm64-", StringComparison.Ordinal))
        {
            pointerBits = 64;
            return true;
        }

        pointerBits = 0;
        return false;
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

        AppendTargetCpuArgument(arguments, targetInfo);

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

    private static void AppendTargetCpuArgument(ICollection<string> arguments, LlvmTargetInfo targetInfo)
    {
        var cpu = targetInfo.Cpu?.Trim();
        if (string.IsNullOrWhiteSpace(cpu)
            || string.Equals(cpu, "generic", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var architecture = targetInfo.Triple.Split('-', 2, StringSplitOptions.TrimEntries)[0];
        var option = architecture.Equals("x86", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("x86_64", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("i386", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("i486", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("i586", StringComparison.OrdinalIgnoreCase)
            || architecture.Equals("i686", StringComparison.OrdinalIgnoreCase)
                ? "-march"
                : "-mcpu";
        arguments.Add($"{option}={cpu}");
    }

    private static void AppendMacOSPlatformSdkArguments(
        ICollection<string> arguments,
        LlvmTargetInfo? targetInfo,
        bool forClangDriver,
        NativeToolchainResolution? toolchain)
    {
        foreach (var argument in GetMacOSPlatformSdkArguments(targetInfo, forClangDriver, toolchain?.MacOSSdkRoot))
        {
            arguments.Add(argument);
        }
    }

    private static IEnumerable<string> GetMacOSPlatformSdkArguments(
        LlvmTargetInfo? targetInfo,
        bool forClangDriver,
        string? resolvedSdkRoot = null)
    {
        resolvedSdkRoot ??= ResolveMacOSSdkRoot();
        if (!ShouldUseMacOSPlatformSdk(targetInfo)
            || resolvedSdkRoot is not { } sdkRoot)
        {
            yield break;
        }

        if (forClangDriver)
        {
            yield return "-isysroot";
            yield return sdkRoot;
        }
        else
        {
            yield return "-syslibroot";
            yield return sdkRoot;
        }
    }

    private static bool ShouldUseMacOSPlatformSdk(LlvmTargetInfo? targetInfo)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (targetInfo is null || string.IsNullOrWhiteSpace(targetInfo.Triple))
        {
            return true;
        }

        return targetInfo.Triple.Contains("apple-darwin", StringComparison.OrdinalIgnoreCase)
            || targetInfo.Triple.Contains("apple-macos", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveMacOSSdkRoot(string? xcrunPath = null)
    {
        var sdkRoot = Environment.GetEnvironmentVariable("SDKROOT");
        if (sdkRoot is not null && IsUsableMacOSSdkRoot(sdkRoot))
        {
            return Path.GetFullPath(sdkRoot);
        }

        foreach (var candidate in new[]
        {
            "/Library/Developer/CommandLineTools/SDKs/MacOSX.sdk",
            "/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk"
        })
        {
            if (IsUsableMacOSSdkRoot(candidate))
            {
                return candidate;
            }
        }

        return QueryXcrunMacOSSdkRoot(xcrunPath);
    }

    private static string? QueryXcrunMacOSSdkRoot(string? xcrunPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(xcrunPath) ? "xcrun" : xcrunPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("--sdk");
            startInfo.ArgumentList.Add("macosx");
            startInfo.ArgumentList.Add("--show-sdk-path");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var standardOutput = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var sdkRoot = standardOutput.Trim();
            return process.ExitCode == 0 && IsUsableMacOSSdkRoot(sdkRoot)
                ? Path.GetFullPath(sdkRoot)
                : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsUsableMacOSSdkRoot(string? sdkRoot)
        => !string.IsNullOrWhiteSpace(sdkRoot) && Directory.Exists(sdkRoot);

    private static bool IsClangDriver(string linkerTool)
        => Path.GetFileNameWithoutExtension(linkerTool).Contains("clang", StringComparison.OrdinalIgnoreCase);

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

    private static void AppendStarkLlvmIrCompileStabilityArguments(
        ICollection<string> arguments,
        string llvmIr,
        bool enableLto)
    {
        if (enableLto
            || RequiresNormalLlvmPassesForImportedInlineBodies(llvmIr))
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

    private static bool RequiresNormalLlvmPassesForImportedInlineBodies(string llvmIr)
    {
        // Imported inline body clones intentionally hand loop-heavy inlining to
        // LLVM; without the always-inliner pass they remain ordinary calls.
        return llvmIr.Contains("__stark_inline_clone_", StringComparison.Ordinal);
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
