namespace Stark.Compiler;

public interface IModuleResolver
{
    bool TryResolveModule(string moduleName, out ResolvedModuleReference module);
}

public interface IModuleSourceResolver : IModuleResolver
{
    bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath);
}

public interface IModuleDocumentResolver : IModuleResolver
{
    bool TryLoadModuleDocument(ResolvedModuleReference module, LlvmTargetInfo? targetInfo, out LoadedModuleDocument document);
}

public sealed class EmptyModuleResolver : IModuleResolver
{
    public static EmptyModuleResolver Instance { get; } = new();

    private EmptyModuleResolver()
    {
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        module = default!;
        return false;
    }
}

public sealed class InMemoryModuleResolver : IModuleSourceResolver
{
    private readonly Dictionary<string, ResolvedModuleReference> _modules;
    private readonly Dictionary<string, (string SourceText, string? FilePath)> _sources;

    public InMemoryModuleResolver(IEnumerable<ResolvedModuleReference> modules)
    {
        _modules = modules.ToDictionary(module => module.ModuleName, StringComparer.Ordinal);
        _sources = new Dictionary<string, (string SourceText, string? FilePath)>(StringComparer.Ordinal);
    }

    public InMemoryModuleResolver(IEnumerable<(ResolvedModuleReference Module, string SourceText, string? FilePath)> modules)
    {
        _modules = modules.ToDictionary(static entry => entry.Module.ModuleName, static entry => entry.Module, StringComparer.Ordinal);
        _sources = modules.ToDictionary(
            static entry => entry.Module.ModuleName,
            static entry => (entry.SourceText, entry.FilePath ?? entry.Module.FilePath),
            StringComparer.Ordinal);
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        return _modules.TryGetValue(moduleName, out module!);
    }

    public bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath)
    {
        if (_sources.TryGetValue(module.ModuleName, out var source))
        {
            sourceText = source.SourceText;
            filePath = source.FilePath;
            return true;
        }

        sourceText = string.Empty;
        filePath = null;
        return false;
    }
}

public sealed class FileSystemModuleResolver : IModuleSourceResolver, IModuleDocumentResolver
{
    private readonly IReadOnlyList<string> _searchDirectories;
    private readonly string? _targetTriple;
    private readonly string? _normalizedTargetTriple;
    private readonly object _manifestIndexLock = new();
    private Dictionary<string, ResolvedPackageModule>? _manifestModules;
    private Dictionary<string, Dictionary<string, ResolvedPackageModule>>? _manifestModulesBySearchDirectory;
    private Dictionary<string, Dictionary<string, ResolvedPackageModule>>? _manifestModulesByPath;

    public FileSystemModuleResolver(string baseDirectory)
        : this([baseDirectory])
    {
    }

    public FileSystemModuleResolver(string baseDirectory, LlvmTargetInfo? targetInfo)
        : this([baseDirectory], targetInfo)
    {
    }

    public FileSystemModuleResolver(IEnumerable<string> searchDirectories)
        : this(searchDirectories, targetInfo: null)
    {
    }

    public FileSystemModuleResolver(IEnumerable<string> searchDirectories, LlvmTargetInfo? targetInfo)
    {
        _searchDirectories = searchDirectories
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .DefaultIfEmpty(Environment.CurrentDirectory)
            .ToArray();
        _targetTriple = string.IsNullOrWhiteSpace(targetInfo?.Triple)
            ? null
            : targetInfo.Triple.Trim();
        _normalizedTargetTriple = _targetTriple is null
            ? null
            : NormalizeTargetPathSegment(_targetTriple);
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        foreach (var searchDirectory in _searchDirectories)
        {
            var filePath = ResolvePath(searchDirectory, moduleName);
            if (File.Exists(filePath))
            {
                module = new ResolvedModuleReference(moduleName, filePath, IsExternal: false);
                return true;
            }

            if (!Directory.Exists(searchDirectory))
            {
                continue;
            }

            EnsureManifestIndex();
            if (_manifestModulesBySearchDirectory!.TryGetValue(Path.GetFullPath(searchDirectory), out var directoryModules)
                && directoryModules.TryGetValue(moduleName, out var manifestModule))
            {
                module = new ResolvedModuleReference(
                    moduleName,
                    FilePath: manifestModule.ManifestPath,
                    IsExternal: false,
                    ManifestPath: manifestModule.ManifestPath,
                    LibraryPath: manifestModule.LibraryPath);
                return true;
            }
        }

        module = default!;
        return false;
    }

    public bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath)
    {
        if (module.ManifestPath is not null)
        {
            if (TryResolveManifestModule(module) is { } manifestModule)
            {
                if (PackageImageLoader.TryBuildStructuredModuleDocument(manifestModule, out var structuredDocument))
                {
                    sourceText = structuredDocument.ParseResult.SourceText;
                    filePath = manifestModule.ManifestPath;
                    return true;
                }

                if (PackageImageLoader.TryBuildModuleSource(manifestModule, out sourceText))
                {
                    filePath = manifestModule.ManifestPath;
                    return true;
                }
            }

            sourceText = string.Empty;
            filePath = module.ManifestPath;
            return false;
        }

        filePath = module.FilePath ?? ResolvePath(_searchDirectories[0], module.ModuleName);
        if (!File.Exists(filePath))
        {
            sourceText = string.Empty;
            return false;
        }

        sourceText = File.ReadAllText(filePath);
        return true;
    }

    public bool TryLoadModuleDocument(ResolvedModuleReference module, LlvmTargetInfo? targetInfo, out LoadedModuleDocument document)
    {
        if (module.ManifestPath is not null
            && TryResolveManifestModule(module) is { } manifestModule
            && (PackageImageLoader.TryBuildStructuredModuleDocument(manifestModule, out var manifestDocument)
                || PackageImageLoader.TryBuildModuleDocument(manifestModule, out manifestDocument)))
        {
            document = manifestDocument with
            {
                Reference = module with { FilePath = manifestModule.ManifestPath },
                TargetInfo = targetInfo
            };
            return true;
        }

        document = default!;
        return false;
    }

    private static string ResolvePath(string baseDirectory, string moduleName)
    {
        var relativePath = moduleName.Replace(".", Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) + ".stark";
        return Path.Combine(baseDirectory, relativePath);
    }

    private ResolvedPackageModule? TryResolveManifestModule(string moduleName)
    {
        EnsureManifestIndex();
        return _manifestModules!.TryGetValue(moduleName, out var module) ? module : null;
    }

    private ResolvedPackageModule? TryResolveManifestModule(ResolvedModuleReference module)
    {
        EnsureManifestIndex();

        if (!string.IsNullOrWhiteSpace(module.ManifestPath)
            && _manifestModulesByPath!.TryGetValue(Path.GetFullPath(module.ManifestPath), out var manifestModules)
            && manifestModules.TryGetValue(module.ModuleName, out var resolvedModule))
        {
            return resolvedModule;
        }

        return TryResolveManifestModule(module.ModuleName);
    }

    private void EnsureManifestIndex()
    {
        if (Volatile.Read(ref _manifestModules) is not null)
        {
            return;
        }

        // One resolver instance is shared by parallel dependency-module compiles, so the
        // index builds once under the lock and publishes through the final volatile write.
        lock (_manifestIndexLock)
        {
            if (_manifestModules is not null)
            {
                return;
            }

            var allModules = new Dictionary<string, ResolvedPackageModule>(StringComparer.Ordinal);
            var modulesBySearchDirectory = new Dictionary<string, Dictionary<string, ResolvedPackageModule>>(StringComparer.Ordinal);
            var modulesByPath = new Dictionary<string, Dictionary<string, ResolvedPackageModule>>(StringComparer.Ordinal);

            foreach (var searchDirectory in _searchDirectories)
            {
                if (!Directory.Exists(searchDirectory))
                {
                    continue;
                }

                var resolvedSearchDirectory = Path.GetFullPath(searchDirectory);
                var directoryModules = new Dictionary<string, ResolvedPackageModule>(StringComparer.Ordinal);
                foreach (var manifestPath in Directory
                             .EnumerateFiles(resolvedSearchDirectory, "*.starkpkg", SearchOption.AllDirectories)
                             .Where(static path => PackageImageBinaryFormat.HasBinaryFileName(path))
                             .Concat(Directory.EnumerateFiles(resolvedSearchDirectory, "*.starkpkg.json", SearchOption.AllDirectories))
                             .Where(path => !IsNestedBuildArtifactManifest(resolvedSearchDirectory, path))
                             .Select(path => (Path: path, Priority: GetManifestTargetPriority(resolvedSearchDirectory, path)))
                             .Where(static entry => entry.Priority >= 0)
                             .OrderBy(static entry => entry.Priority)
                             .ThenBy(static entry => !PackageImageBinaryFormat.HasBinaryFileName(entry.Path))
                             .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
                             .Select(static entry => entry.Path))
                {
                    if (!PackageImageLoader.TryLoadManifest(manifestPath, out var manifest))
                    {
                        continue;
                    }

                    var resolvedManifestPath = Path.GetFullPath(manifestPath);
                    var libraryPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resolvedManifestPath) ?? resolvedSearchDirectory, manifest.LibraryFileName));
                    var manifestModules = new Dictionary<string, ResolvedPackageModule>(StringComparer.Ordinal);

                    foreach (var module in manifest.Modules)
                    {
                        var resolvedModule = new ResolvedPackageModule(
                            resolvedManifestPath,
                            libraryPath,
                            manifest,
                            module);
                        directoryModules.TryAdd(module.ModuleName, resolvedModule);
                        manifestModules[module.ModuleName] = resolvedModule;
                        allModules.TryAdd(module.ModuleName, resolvedModule);
                    }

                    modulesByPath[resolvedManifestPath] = manifestModules;
                }

                if (directoryModules.Count != 0)
                {
                    modulesBySearchDirectory[resolvedSearchDirectory] = directoryModules;
                }
            }

            _manifestModulesBySearchDirectory = modulesBySearchDirectory;
            _manifestModulesByPath = modulesByPath;
            Volatile.Write(ref _manifestModules, allModules);
        }
    }

    private int GetManifestTargetPriority(string searchDirectory, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(_targetTriple))
        {
            return 1;
        }

        var relativePath = Path.GetRelativePath(searchDirectory, manifestPath);
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1 || !LooksLikeTargetTripleDirectory(segments[0]))
        {
            return 1;
        }

        return IsActiveTargetDirectory(segments[0])
            ? 0
            : -1;
    }

    private bool IsActiveTargetDirectory(string segment)
    {
        return string.Equals(segment, _targetTriple, StringComparison.Ordinal)
            || string.Equals(segment, _normalizedTargetTriple, StringComparison.Ordinal);
    }

    private static bool LooksLikeTargetTripleDirectory(string segment)
    {
        if (!segment.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        var firstComponentLength = segment.IndexOf('-', StringComparison.Ordinal);
        var architecture = firstComponentLength <= 0 ? segment : segment[..firstComponentLength];
        return IsKnownTargetArchitecture(architecture)
            || segment.Contains("linux", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("win32", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("mingw", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("msvc", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("darwin", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("macos", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("freebsd", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("netbsd", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("openbsd", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("dragonfly", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("haiku", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("solaris", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("illumos", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("android", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("ios", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("wasi", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("wasm", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("emscripten", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownTargetArchitecture(string architecture)
    {
        return architecture.StartsWith("x86", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i386", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i486", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i586", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("i686", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("amd64", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("arm", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("aarch64", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("riscv", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("mips", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("powerpc", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("ppc", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("s390x", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("sparc", StringComparison.OrdinalIgnoreCase)
            || architecture.StartsWith("wasm", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetPathSegment(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '+'
                ? ch
                : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static bool IsNestedBuildArtifactManifest(string searchDirectory, string manifestPath)
    {
        if (IsInsideStarkDirectory(searchDirectory))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(searchDirectory, manifestPath);
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static segment => string.Equals(segment, ".stark", StringComparison.Ordinal));
    }

    private static bool IsInsideStarkDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            if (string.Equals(directory.Name, ".stark", StringComparison.Ordinal))
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }
}

public sealed class TargetAwareStdLibModuleResolver : IModuleSourceResolver, IModuleDocumentResolver
{
    private const string PlatformModuleName = "System.Runtime.Platform";
    private const string LinuxDispatchTemplateRelativePath = "templates/System.Runtime.Platform.LinuxDispatch.stark";
    private const string MacOSDispatchTemplateRelativePath = "templates/System.Runtime.Platform.MacOSDispatch.stark";
    private const string WindowsDispatchTemplateRelativePath = "templates/System.Runtime.Platform.WindowsDispatch.stark";

    private readonly IModuleSourceResolver _inner;
    private readonly string? _dispatchTemplatePath;

    public TargetAwareStdLibModuleResolver(
        IModuleSourceResolver inner,
        IEnumerable<string> searchDirectories,
        LlvmTargetInfo? targetInfo)
    {
        _inner = inner;
        _dispatchTemplatePath = FindDispatchTemplate(searchDirectories, targetInfo);
    }

    public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
    {
        if (ShouldOverridePlatformModule(moduleName))
        {
            module = new ResolvedModuleReference(moduleName, _dispatchTemplatePath, IsExternal: false);
            return true;
        }

        return _inner.TryResolveModule(moduleName, out module);
    }

    public bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath)
    {
        if (ShouldOverridePlatformModule(module.ModuleName))
        {
            filePath = _dispatchTemplatePath;
            if (filePath is null || !File.Exists(filePath))
            {
                sourceText = string.Empty;
                filePath = null;
                return false;
            }

            sourceText = File.ReadAllText(filePath);
            return true;
        }

        return _inner.TryLoadModuleSource(module, out sourceText, out filePath);
    }

    public bool TryLoadModuleDocument(ResolvedModuleReference module, LlvmTargetInfo? targetInfo, out LoadedModuleDocument document)
    {
        if (ShouldOverridePlatformModule(module.ModuleName))
        {
            document = default!;
            return false;
        }

        if (_inner is IModuleDocumentResolver documentResolver)
        {
            return documentResolver.TryLoadModuleDocument(module, targetInfo, out document);
        }

        document = default!;
        return false;
    }

    private bool ShouldOverridePlatformModule(string moduleName)
    {
        return !string.IsNullOrWhiteSpace(_dispatchTemplatePath)
            && string.Equals(moduleName, PlatformModuleName, StringComparison.Ordinal);
    }

    private static bool IsWindowsTarget(LlvmTargetInfo? targetInfo)
    {
        return targetInfo?.Triple.Contains("-windows-", StringComparison.OrdinalIgnoreCase) == true
            || targetInfo?.Triple.EndsWith("-windows", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsMacOSTarget(LlvmTargetInfo? targetInfo)
    {
        return targetInfo?.Triple.Contains("darwin", StringComparison.OrdinalIgnoreCase) == true
            || targetInfo?.Triple.Contains("macos", StringComparison.OrdinalIgnoreCase) == true
            || targetInfo?.Triple.Contains("macosx", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? FindDispatchTemplate(IEnumerable<string> searchDirectories, LlvmTargetInfo? targetInfo)
    {
        var templateRelativePath = IsMacOSTarget(targetInfo)
            ? MacOSDispatchTemplateRelativePath
            : IsWindowsTarget(targetInfo)
                ? WindowsDispatchTemplateRelativePath
                : LinuxDispatchTemplateRelativePath;

        foreach (var searchDirectory in searchDirectories)
        {
            var resolvedSearchDirectory = Path.GetFullPath(searchDirectory);
            if (!Directory.Exists(resolvedSearchDirectory))
            {
                continue;
            }

            var standardLibraryRoot = TryFindStdLibRoot(resolvedSearchDirectory);
            if (standardLibraryRoot is null)
            {
                continue;
            }

            var templatePath = Path.Combine(standardLibraryRoot, templateRelativePath);
            if (File.Exists(templatePath))
            {
                return templatePath;
            }
        }

        return null;
    }

    private static string? TryFindStdLibRoot(string searchDirectory)
    {
        var directory = new DirectoryInfo(searchDirectory);

        while (directory is not null)
        {
            if (IsStdLibRoot(directory))
            {
                return directory.FullName;
            }

            if ((string.Equals(directory.Name, "src", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(directory.Name, "dist", StringComparison.OrdinalIgnoreCase))
                && directory.Parent is not null
                && IsStdLibRoot(directory.Parent))
            {
                return directory.Parent.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsStdLibRoot(DirectoryInfo directory)
    {
        return string.Equals(directory.Name, "stdlib", StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(Path.Combine(directory.FullName, "templates"))
                || Directory.Exists(Path.Combine(directory.FullName, "src"))
                || Directory.Exists(Path.Combine(directory.FullName, "dist"))
                || File.Exists(Path.Combine(directory.FullName, "Stark.toml")));
    }
}
