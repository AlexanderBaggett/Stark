using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Stark.Compiler;

internal enum DoctorOutputFormat
{
    Text,
    Json
}

internal sealed record CompilerDoctorOptions(
    LlvmTargetInfo? TargetInfo,
    LlvmRelocationModel RequestedRelocationModel,
    LlvmCodeModel? RequestedCodeModel,
    string? LinkerTool,
    string? ArchiverTool,
    NativeToolchainResolution Toolchain,
    IReadOnlyList<string> NativePkgConfigPackages,
    bool HasNativeDependencies,
    bool RequiresLlvmLibrary,
    bool UseStarkPathEnvironment,
    ActiveSdkResolution ActiveSdk,
    DoctorOutputFormat OutputFormat,
    bool Strict);

internal sealed record DoctorCompilerReport(
    string Version,
    string Path,
    string SdkCompatibility);

internal sealed record DoctorRuntimeReport(
    string RuntimeId,
    string Framework,
    string OperatingSystem,
    string ProcessArchitecture);

internal sealed record DoctorCDataModelReport(
    string Kind,
    bool CharIsSigned,
    int PointerBitWidth,
    int LongBitWidth,
    int SizeTBitWidth,
    int PtrDiffTBitWidth);

internal sealed record DoctorTargetReport(
    string? Triple,
    string? DataLayout,
    string? Cpu,
    IReadOnlyList<string> Features,
    string RelocationModel,
    string? CodeModel,
    DoctorCDataModelReport? CDataModel);

internal sealed record DoctorResolvedToolReport(
    string Role,
    string Status,
    string RequestedName,
    string? Path,
    string Source,
    string? Version);

internal sealed record DoctorResolvedFileReport(
    string Role,
    string Status,
    string RequestedName,
    string? Path,
    string Source);

internal sealed record DoctorToolchainReport(
    IReadOnlyList<string> SearchRoots,
    IReadOnlyList<DoctorResolvedToolReport> Tools,
    IReadOnlyList<DoctorResolvedFileReport> Files);

internal sealed record DoctorCompilerBackendReport(
    string Mode,
    string Status,
    IReadOnlyList<DoctorResolvedToolReport> Tools,
    IReadOnlyList<DoctorResolvedFileReport> Files);

internal sealed record DoctorHostDevelopmentReport(
    string Name,
    string Status,
    string Requirement,
    DoctorResolvedToolReport Linker,
    DoctorPlatformSdkReport PlatformSdk);

internal sealed record DoctorPlatformSdkReport(
    string Name,
    string Status,
    string? Path,
    bool Required);

internal sealed record DoctorLibraryReport(
    string Name,
    string Status,
    string? BundledPath,
    string? RepositoryDistributionPath,
    string? RepositorySourcePath,
    string? StarkPath,
    bool StarkPathEnabled);

internal sealed record DoctorDiagnosticReport(
    string Severity,
    string Area,
    string Message);

internal sealed record CompilerDoctorReport(
    int SchemaVersion,
    string Status,
    bool Strict,
    DoctorCompilerReport Compiler,
    DoctorRuntimeReport Runtime,
    DoctorTargetReport Target,
    DoctorCompilerBackendReport CompilerBackend,
    DoctorHostDevelopmentReport HostDevelopment,
    DoctorToolchainReport Toolchain,
    SdkDoctorReport Sdk,
    DoctorPlatformSdkReport PlatformSdk,
    IReadOnlyList<DoctorLibraryReport> Libraries,
    IReadOnlyList<DoctorDiagnosticReport> Diagnostics);

/// <summary>
/// Collects doctor state once, then renders either stable human text or a
/// deterministic JSON document. Probes never write while the report is being
/// built, which keeps machine output free of incidental text.
/// </summary>
internal static class CompilerDoctor
{
    private const int ReportSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(TextWriter stdout, CompilerDoctorOptions options)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(options);

        var report = await BuildReportAsync(options);
        if (options.OutputFormat == DoctorOutputFormat.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            WriteText(stdout, report);
        }

        return options.Strict && !string.Equals(report.Status, "ok", StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private static async Task<CompilerDoctorReport> BuildReportAsync(CompilerDoctorOptions options)
    {
        var assembly = typeof(CompilerDoctor).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "<unknown>";
        var target = BuildTargetReport(
            options.TargetInfo,
            options.RequestedRelocationModel,
            options.RequestedCodeModel);

        var tools = new[]
        {
            await ProbeToolAsync(options.Toolchain.Clang, "--version"),
            await ProbeToolAsync(options.Toolchain.Linker, "--version"),
            await ProbeToolAsync(options.Toolchain.Archiver, "--version"),
            await ProbeToolAsync(options.Toolchain.Lld, "--version"),
            await ProbeToolAsync(options.Toolchain.PkgConfig, "--version"),
            await ProbeToolAsync(options.Toolchain.Xcrun, "--version")
        };
        var llvm = ToResolvedFileReport(options.Toolchain.LlvmLibrary);
        var sdk = SdkDiagnostics.BuildDoctorReport(options.ActiveSdk, options.TargetInfo);
        var platformSdk = BuildPlatformSdkReport(options.TargetInfo, options.Toolchain);
        var libraries = new[]
        {
            InspectLibrary("stdlib", options.UseStarkPathEnvironment, options.ActiveSdk),
            InspectLibrary("vendor", options.UseStarkPathEnvironment, options.ActiveSdk)
        };

        var clangOk = IsAvailable(tools, "clang");
        var linkerOk = IsAvailable(tools, "linker");
        var archiverOk = IsAvailable(tools, "archiver");
        var lldOk = IsAvailable(tools, "lld");
        var pkgConfigOk = IsAvailable(tools, "pkg-config");
        var xcrunOk = !OperatingSystem.IsMacOS() || IsAvailable(tools, "xcrun");
        var diagnostics = BuildDiagnostics(
            options,
            clangOk,
            linkerOk,
            archiverOk,
            lldOk,
            pkgConfigOk,
            llvm.Status == "ok",
            xcrunOk,
            sdk.IsValid,
            platformSdk.Status == "ok",
            libraries[0].Status == "ok",
            libraries[1].Status == "ok").ToArray();
        var hasWarnings = diagnostics.Any(static diagnostic => diagnostic.Severity == "warning")
            || options.TargetInfo is null
            || !sdk.IsValid;

        return new CompilerDoctorReport(
            ReportSchemaVersion,
            hasWarnings ? "warnings" : "ok",
            options.Strict,
            new DoctorCompilerReport(
                informationalVersion,
                assembly.Location,
                SdkCompilerCompatibility.SupportedLine),
            new DoctorRuntimeReport(
                RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString()),
            target,
            BuildCompilerBackendReport(options, tools, llvm),
            BuildHostDevelopmentReport(options.TargetInfo, tools, platformSdk),
            new DoctorToolchainReport(
                options.Toolchain.SearchRoots.ToArray(),
                tools,
                [llvm]),
            sdk,
            platformSdk,
            libraries,
            diagnostics);
    }

    private static void WriteText(TextWriter stdout, CompilerDoctorReport report)
    {
        stdout.WriteLine("Stark doctor");
        stdout.WriteLine("compiler:");
        stdout.WriteLine($"  version: {report.Compiler.Version}");
        stdout.WriteLine($"  path: {report.Compiler.Path}");
        stdout.WriteLine($"  sdk compatibility: {report.Compiler.SdkCompatibility}");
        stdout.WriteLine("runtime:");
        stdout.WriteLine($"  runtime id: {report.Runtime.RuntimeId}");
        stdout.WriteLine($"  framework: {report.Runtime.Framework}");
        stdout.WriteLine($"  os: {report.Runtime.OperatingSystem}");
        stdout.WriteLine($"  process architecture: {report.Runtime.ProcessArchitecture}");
        stdout.WriteLine("target:");
        stdout.WriteLine($"  triple: {report.Target.Triple ?? "<unresolved>"}");
        stdout.WriteLine($"  data layout: {FormatOptional(report.Target.DataLayout)}");
        stdout.WriteLine($"  cpu: {FormatOptional(report.Target.Cpu)}");
        stdout.WriteLine($"  features: {FormatList(report.Target.Features, "<default>")}");
        stdout.WriteLine($"  relocation model: {report.Target.RelocationModel}");
        stdout.WriteLine($"  code model: {report.Target.CodeModel ?? "<default>"}");
        stdout.WriteLine($"  c data model: {FormatCDataModel(report.Target.CDataModel)}");
        stdout.WriteLine("compiler-private backend:");
        stdout.WriteLine($"  mode: {report.CompilerBackend.Mode}");
        stdout.WriteLine($"  status: {report.CompilerBackend.Status}");
        foreach (var tool in report.CompilerBackend.Tools)
        {
            WriteResolvedTool(stdout, tool, "  ");
        }

        foreach (var file in report.CompilerBackend.Files)
        {
            WriteResolvedFile(stdout, file, "  ");
        }

        stdout.WriteLine("host development layer:");
        stdout.WriteLine($"  name: {report.HostDevelopment.Name}");
        stdout.WriteLine($"  status: {report.HostDevelopment.Status}");
        stdout.WriteLine($"  requirement: {report.HostDevelopment.Requirement}");
        WriteResolvedTool(stdout, report.HostDevelopment.Linker, "  ");
        stdout.WriteLine(report.HostDevelopment.PlatformSdk.Required
            ? $"  platform sdk: {report.HostDevelopment.PlatformSdk.Path ?? "<missing>"}"
            : "  platform sdk: not separately probed for this target");
        stdout.WriteLine("toolchain:");

        if (report.Toolchain.SearchRoots.Count == 0)
        {
            stdout.WriteLine("  search roots: <none>");
        }
        else
        {
            stdout.WriteLine("  search roots:");
            foreach (var root in report.Toolchain.SearchRoots)
            {
                stdout.WriteLine($"    {root}");
            }
        }

        foreach (var tool in report.Toolchain.Tools)
        {
            WriteResolvedTool(stdout, tool, "  ");
        }

        foreach (var file in report.Toolchain.Files)
        {
            WriteResolvedFile(stdout, file, "  ");
        }

        stdout.WriteLine("sdk:");
        SdkDiagnostics.WriteText(stdout, report.Sdk);
        stdout.WriteLine("platform sdk:");
        stdout.WriteLine(report.PlatformSdk.Required
            ? $"  {report.PlatformSdk.Name}: {report.PlatformSdk.Path ?? "<missing>"}"
            : $"  {report.PlatformSdk.Name}: not required for this host/target");
        stdout.WriteLine("libraries:");
        foreach (var library in report.Libraries)
        {
            stdout.WriteLine($"  {library.Name}:");
            stdout.WriteLine($"    bundled: {library.BundledPath ?? "<missing>"}");
            stdout.WriteLine($"    repo dist: {library.RepositoryDistributionPath ?? "<missing>"}");
            stdout.WriteLine($"    repo src: {library.RepositorySourcePath ?? "<missing>"}");
            stdout.WriteLine(library.StarkPathEnabled
                ? $"    STARK_PATH: {library.StarkPath ?? "<default>"}"
                : "    STARK_PATH: ignored by --no-stark-path");
        }

        stdout.WriteLine("diagnostics:");
        if (report.Diagnostics.Count == 0)
        {
            stdout.WriteLine("  none");
        }
        else
        {
            foreach (var diagnostic in report.Diagnostics)
            {
                stdout.WriteLine($"  {diagnostic.Severity}: {diagnostic.Area}: {diagnostic.Message}");
            }
        }

        stdout.WriteLine($"status: {report.Status}");
    }

    private static DoctorTargetReport BuildTargetReport(
        LlvmTargetInfo? targetInfo,
        LlvmRelocationModel requestedRelocationModel,
        LlvmCodeModel? requestedCodeModel)
    {
        DoctorCDataModelReport? cDataModel = null;
        if (StarkCDataModelFacts.TryResolve(targetInfo, out var resolvedCDataModel))
        {
            cDataModel = new DoctorCDataModelReport(
                resolvedCDataModel.Kind.ToString(),
                resolvedCDataModel.CharIsSigned,
                resolvedCDataModel.PointerBitWidth,
                resolvedCDataModel.LongBitWidth,
                resolvedCDataModel.SizeTBitWidth,
                resolvedCDataModel.PtrDiffTBitWidth);
        }

        return new DoctorTargetReport(
            targetInfo?.Triple,
            targetInfo?.DataLayout,
            targetInfo?.Cpu,
            targetInfo?.Features?.ToArray() ?? Array.Empty<string>(),
            (targetInfo?.RelocationModel ?? requestedRelocationModel).ToString().ToLowerInvariant(),
            (targetInfo?.CodeModel ?? requestedCodeModel)?.ToString().ToLowerInvariant(),
            cDataModel);
    }

    private static DoctorCompilerBackendReport BuildCompilerBackendReport(
        CompilerDoctorOptions options,
        IReadOnlyList<DoctorResolvedToolReport> tools,
        DoctorResolvedFileReport llvm)
    {
        var clang = FindTool(tools, "clang");
        var archiver = FindTool(tools, "archiver");
        var lld = FindTool(tools, "lld");
        var primaryAvailable = options.RequiresLlvmLibrary
            ? string.Equals(llvm.Status, "ok", StringComparison.Ordinal)
            : string.Equals(clang.Status, "ok", StringComparison.Ordinal);
        var archiveAvailable = string.Equals(archiver.Status, "ok", StringComparison.Ordinal);
        var optimizedLinkAvailable = string.Equals(lld.Status, "ok", StringComparison.Ordinal);
        var status = !primaryAvailable || !archiveAvailable
            ? "missing"
            : optimizedLinkAvailable
                ? "ok"
                : "degraded";

        return new DoctorCompilerBackendReport(
            options.RequiresLlvmLibrary ? "direct-libllvm" : "stage0-textual-llvm",
            status,
            [clang, archiver, lld],
            [llvm]);
    }

    private static DoctorHostDevelopmentReport BuildHostDevelopmentReport(
        LlvmTargetInfo? targetInfo,
        IReadOnlyList<DoctorResolvedToolReport> tools,
        DoctorPlatformSdkReport platformSdk)
    {
        var linker = FindTool(tools, "linker");
        var linkerAvailable = string.Equals(linker.Status, "ok", StringComparison.Ordinal);
        if (NativeToolchain.ShouldUseMacOSPlatformSdkForTarget(targetInfo))
        {
            return new DoctorHostDevelopmentReport(
                "macos",
                linkerAvailable && string.Equals(platformSdk.Status, "ok", StringComparison.Ordinal)
                    ? "ok"
                    : "missing",
                "Xcode Command Line Tools or full Xcode supplies the macOS SDK and platform link surface.",
                linker,
                platformSdk);
        }

        if (IsWindowsTarget(targetInfo))
        {
            return new DoctorHostDevelopmentReport(
                "windows-msvc",
                linkerAvailable ? "unverified-sdk" : "missing",
                "A supported MSVC Build Tools and Windows SDK installation supplies SDK/UCRT import libraries; the final link verifies the selected installation.",
                linker,
                platformSdk);
        }

        if (IsLinuxTarget(targetInfo))
        {
            return new DoctorHostDevelopmentReport(
                "linux-native",
                linkerAvailable ? "ok" : "missing",
                "A supported Clang/native development environment and system ABI libraries supply the final host link layer.",
                linker,
                platformSdk);
        }

        return new DoctorHostDevelopmentReport(
            "target-native",
            linkerAvailable ? "unverified" : "missing",
            "The target's documented native development environment supplies final-link platform inputs.",
            linker,
            platformSdk);
    }

    private static DoctorResolvedToolReport FindTool(
        IReadOnlyList<DoctorResolvedToolReport> tools,
        string role) => tools.Single(tool => string.Equals(tool.Role, role, StringComparison.Ordinal));

    private static void WriteResolvedTool(
        TextWriter stdout,
        DoctorResolvedToolReport tool,
        string indent)
    {
        if (tool.Status == "ok")
        {
            stdout.WriteLine($"{indent}{tool.Role}: {tool.Path} ({FormatSource(tool.Source)}, {tool.Version})");
        }
        else
        {
            stdout.WriteLine($"{indent}{tool.Role}: <missing> ({tool.RequestedName}, {FormatSource(tool.Source)})");
        }
    }

    private static void WriteResolvedFile(
        TextWriter stdout,
        DoctorResolvedFileReport file,
        string indent)
    {
        if (file.Status == "ok")
        {
            stdout.WriteLine($"{indent}{file.Role}: {file.Path} ({FormatSource(file.Source)})");
        }
        else
        {
            stdout.WriteLine($"{indent}{file.Role}: <missing> ({file.RequestedName}, {FormatSource(file.Source)})");
        }
    }

    private static async Task<DoctorResolvedToolReport> ProbeToolAsync(
        NativeResolvedTool tool,
        string versionArgument)
    {
        if (!tool.IsAvailable)
        {
            return new DoctorResolvedToolReport(
                tool.Role,
                "missing",
                tool.RequestedName,
                Path: null,
                FormatSourceValue(tool.Source),
                Version: null);
        }

        return new DoctorResolvedToolReport(
            tool.Role,
            "ok",
            tool.RequestedName,
            tool.Path,
            FormatSourceValue(tool.Source),
            await ReadToolVersionAsync(tool.Path!, versionArgument));
    }

    private static DoctorResolvedFileReport ToResolvedFileReport(NativeResolvedFile file) =>
        new(
            file.Role,
            file.IsAvailable ? "ok" : "missing",
            file.RequestedName,
            file.Path,
            FormatSourceValue(file.Source));

    private static DoctorPlatformSdkReport BuildPlatformSdkReport(
        LlvmTargetInfo? targetInfo,
        NativeToolchainResolution toolchain)
    {
        if (!NativeToolchain.ShouldUseMacOSPlatformSdkForTarget(targetInfo))
        {
            return new DoctorPlatformSdkReport("macos", "ok", Path: null, Required: false);
        }

        return NativeToolchain.TryResolveMacOSSdkRoot(out var sdkRoot, toolchain)
            ? new DoctorPlatformSdkReport("macos", "ok", sdkRoot, Required: true)
            : new DoctorPlatformSdkReport("macos", "missing", Path: null, Required: true);
    }

    private static DoctorLibraryReport InspectLibrary(
        string rootName,
        bool useStarkPathEnvironment,
        ActiveSdkResolution activeSdk)
    {
        var compilerDirectory = Path.GetDirectoryName(typeof(CompilerDoctor).Assembly.Location);
        var sdkRootDirectory = activeSdk.Root?.RootPath;
        var bundledPath = ExistingDirectory(
                sdkRootDirectory is null ? null : Path.Combine(sdkRootDirectory, rootName))
            ?? ExistingDirectory(
                compilerDirectory is null ? null : Path.Combine(compilerDirectory, rootName));
        var repositoryDistributionPath = ExistingDirectory(FindNearestExistingDirectory(rootName, "dist"));
        var repositorySourcePath = ExistingDirectory(FindNearestExistingDirectory(rootName, "src"));
        var starkPath = useStarkPathEnvironment
            ? NullIfWhiteSpace(Environment.GetEnvironmentVariable("STARK_PATH"))
            : null;
        var available = bundledPath is not null
            || repositoryDistributionPath is not null
            || repositorySourcePath is not null
            || starkPath is not null;

        return new DoctorLibraryReport(
            rootName,
            available ? "ok" : "missing",
            bundledPath,
            repositoryDistributionPath,
            repositorySourcePath,
            starkPath,
            useStarkPathEnvironment);
    }

    private static IEnumerable<DoctorDiagnosticReport> BuildDiagnostics(
        CompilerDoctorOptions options,
        bool clangOk,
        bool linkerOk,
        bool archiverOk,
        bool lldOk,
        bool pkgConfigOk,
        bool llvmOk,
        bool xcrunOk,
        bool starkSdkOk,
        bool platformSdkOk,
        bool stdlibOk,
        bool vendorOk)
    {
        if (options.TargetInfo is null)
        {
            yield return Warning(
                "target",
                "default target facts could not be detected; pass --target and --target-data-layout when building without a detectable clang target.");
        }

        if (!clangOk && !options.RequiresLlvmLibrary)
        {
            yield return Warning(
                "compiler backend",
                "the Stage0 private Clang backend is missing; repair or re-extract the complete Stark SDK, or use the advanced --toolchain-dir/STARK_TOOLCHAIN_DIR/STARK_CLANG override while developing the compiler.");
        }
        else if (!clangOk && options.HasNativeDependencies)
        {
            yield return Warning(
                "host native compiler",
                "Clang is missing; the direct libLLVM backend can emit Stark objects, but native source dependencies cannot be compiled for this invocation.");
        }
        else if (!clangOk)
        {
            yield return Note(
                "host native compiler",
                "Clang is not selected; the direct libLLVM backend remains usable, but packages with native source inputs require the documented host native compiler.");
        }

        if (!linkerOk)
        {
            var overrideHint = string.IsNullOrWhiteSpace(options.LinkerTool)
                ? "install the target's documented host development layer or pass the advanced --linker override"
                : $"the --linker override '{options.LinkerTool.Trim()}' was not found";
            yield return Warning(
                "host linker",
                $"executable links cannot run because the linker is missing; {overrideHint}.");
        }

        if (!archiverOk)
        {
            var overrideHint = string.IsNullOrWhiteSpace(options.ArchiverTool)
                ? "repair the compiler-private backend or pass the advanced --archiver override"
                : $"the --archiver override '{options.ArchiverTool.Trim()}' was not found";
            yield return Warning(
                "compiler backend",
                $"static-library output cannot run because the archiver is missing; {overrideHint}.");
        }

        if (!lldOk)
        {
            yield return Warning(
                "compiler backend optimization",
                "the private LLD component is missing, so ThinLTO and the fastest executable link path are unavailable; repair the Stark SDK or use an advanced compiler-development override.");
        }

        if (!llvmOk && options.RequiresLlvmLibrary)
        {
            yield return Warning(
                "compiler backend",
                "the compiler-private libLLVM runtime is missing; repair or re-extract the Stark SDK, or use the advanced --llvm-lib/STARK_LLVM_LIB/--toolchain-dir override while developing the compiler.");
        }
        else if (!llvmOk)
        {
            yield return Note(
                "compiler backend",
                "the active Stage0 textual LLVM backend does not require libLLVM; a direct Stage1 backend must carry its matching private libLLVM runtime.");
        }

        if (!xcrunOk)
        {
            yield return Warning(
                "macos sdk",
                "xcrun is missing; install Xcode Command Line Tools or full Xcode so macOS SDK discovery can run.");
        }

        if (!starkSdkOk)
        {
            yield return Warning(
                "stark sdk",
                "sdk.json is missing, incompatible, or incomplete; use a complete Stark archive, or select a development SDK with --sdk-root/STARK_SDK_ROOT.");
        }

        if (!platformSdkOk)
        {
            yield return Warning(
                "macos sdk",
                "the macOS SDK root is missing; install Xcode Command Line Tools, install full Xcode, or set SDKROOT to a usable SDK.");
        }

        if (!stdlibOk)
        {
            yield return Warning(
                "stdlib",
                "the standard library was not found in the bundled archive, repo development roots, or STARK_PATH.");
        }

        if (!vendorOk)
        {
            yield return Warning(
                "vendor",
                "the official vendor library was not found in the bundled archive, repo development roots, or STARK_PATH.");
        }

        if (IsWindowsTarget(options.TargetInfo))
        {
            yield return Note(
                "windows crt",
                "Windows executable links use the current linker-driver path and require Windows SDK/CRT import libraries to be visible to that driver.");
        }

        if (IsLinuxTarget(options.TargetInfo))
        {
            yield return Note(
                "linux libc",
                "Stark-owned Linux stdlib/runtime code uses syscalls and does not require libc/glibc; native or vendor dependencies may still require their own system libraries.");
        }

        if (!pkgConfigOk && options.NativePkgConfigPackages.Count != 0)
        {
            yield return Warning(
                "pkg-config",
                "pkg-config is unavailable, but this invocation declares pkg-config packages; install it or replace those dependencies with explicit relocatable metadata.");
        }

        if (options.NativePkgConfigPackages.Count != 0)
        {
            yield return Note(
                "pkg-config",
                $"this invocation declares pkg-config packages: {string.Join(", ", options.NativePkgConfigPackages)}.");
        }

        if (options.HasNativeDependencies)
        {
            yield return Note(
                "native dependencies",
                "this invocation declares native dependency metadata; verify source, include, library, and runtime paths for the selected target.");
        }

        yield return Note(
            "native/vendor dependencies",
            "official SDK package images carry their native payloads and ordered link facts without pkg-config; package-author source builds may still use explicit discovery inputs and platform fallbacks.");
    }

    private static async Task<string> ReadToolVersionAsync(string toolPath, string versionArgument)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(versionArgument);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "version unavailable";
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(2000);
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort timeout cleanup only.
                }

                return "version timed out";
            }

            var standardOutput = await stdoutTask;
            var standardError = await stderrTask;
            var text = standardOutput.Length != 0 ? standardOutput : standardError;
            var firstLine = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstLine) ? "version unavailable" : firstLine;
        }
        catch
        {
            return "version unavailable";
        }
    }

    private static string? FindNearestExistingDirectory(string rootName, string childName)
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, rootName, childName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath) ? fullPath : null;
    }

    private static bool IsAvailable(IReadOnlyList<DoctorResolvedToolReport> tools, string role) =>
        tools.Single(tool => string.Equals(tool.Role, role, StringComparison.Ordinal)).Status == "ok";

    private static string FormatSourceValue(NativeToolchainResolutionSource source) =>
        source switch
        {
            NativeToolchainResolutionSource.CliOverride => "cli",
            NativeToolchainResolutionSource.EnvironmentOverride => "environment",
            NativeToolchainResolutionSource.UserConfig => "user-config",
            NativeToolchainResolutionSource.Bundled => "bundled",
            NativeToolchainResolutionSource.Path => "path",
            NativeToolchainResolutionSource.Missing => "missing",
            _ => source.ToString().ToLowerInvariant()
        };

    private static string FormatSource(string source) =>
        source == "user-config" ? "user config" : source == "path" ? "PATH" : source;

    private static string FormatOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<default>" : value.Trim();

    private static string FormatList(IEnumerable<string> values, string emptyValue)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? emptyValue : string.Join(", ", materialized);
    }

    private static string FormatCDataModel(DoctorCDataModelReport? dataModel) =>
        dataModel is null
            ? "<unresolved>"
            : $"{dataModel.Kind}, pointer={dataModel.PointerBitWidth}, long={dataModel.LongBitWidth}, size_t={dataModel.SizeTBitWidth}, ptrdiff_t={dataModel.PtrDiffTBitWidth}, c_char={(dataModel.CharIsSigned ? "signed" : "unsigned")}";

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsWindowsTarget(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo?.Triple is { Length: > 0 } triple)
        {
            return triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
                || triple.Contains("mingw", StringComparison.OrdinalIgnoreCase);
        }

        return OperatingSystem.IsWindows();
    }

    private static bool IsLinuxTarget(LlvmTargetInfo? targetInfo)
    {
        if (targetInfo?.Triple is { Length: > 0 } triple)
        {
            return triple.Contains("linux", StringComparison.OrdinalIgnoreCase);
        }

        return OperatingSystem.IsLinux();
    }

    private static DoctorDiagnosticReport Warning(string area, string message) =>
        new("warning", area, message);

    private static DoctorDiagnosticReport Note(string area, string message) =>
        new("note", area, message);
}
